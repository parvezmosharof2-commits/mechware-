using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Npgsql;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Identity;
using Mechware.Domain.Sellers;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;

namespace Mechware.Infrastructure.Catalog;

/// <summary>
/// Bulk import, commit (C10.2). Runs the C10.1 checks first; any problem and nothing is written. Otherwise it creates draft
/// products and variants and updates existing ones (matched by slug / part number key / SKU) through the domain methods, in one
/// transaction behind a database-wide advisory lock, so two overlapping imports run one after the other and the second one sees the
/// first one's rows. Only values present in the file are touched, and only when they differ, so the same file again changes nothing.
/// Search and cache freshness come from the product events the domain methods raise (outbox). Format: docs/catalog/IMPORT_FORMAT.md.
/// </summary>
public sealed class CatalogImportCommitter(MechwareDbContext db, ICatalogImportValidator validator, ICurrentUserService currentUser)
    : ICatalogImportCommitter
{
    private const string LockKey = "catalog-import";
    private const int BatchProducts = 500;

    private sealed class Totals
    {
        public int CreatedProducts, CreatedVariants, UpdatedProducts, UpdatedVariants, UnchangedProducts, UnchangedVariants;
    }

    public async Task<CatalogImportCommitOutcome> CommitAsync(Stream csv, string sha256, Guid sellerId, CancellationToken ct = default)
    {
        if (!await db.Sellers.AnyAsync(s => s.Id == sellerId && s.Status == SellerStatus.Approved, ct))
            throw new ValidationException("sellerId", "The seller does not exist or is not approved.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({LockKey}, 0))", ct);

        // Inside the lock, so it sees what an import that ran just before this one committed.
        var analysis = await validator.AnalyseAsync(csv, ct);
        if (analysis.Report.ErrorCount > 0) return new CatalogImportCommitOutcome(null, analysis.Report);

        var totals = new Totals();
        var errors = new List<CatalogImportRowError>();
        try
        {
            await WriteAsync(analysis.Rows, sellerId, totals, errors, ct);
            if (errors.Count > 0)
            {
                await tx.RollbackAsync(ct);
                db.ChangeTracker.Clear();
                return new CatalogImportCommitOutcome(null, analysis.Report with
                {
                    ValidRows = analysis.Report.Rows - errors.Select(e => e.Row).Distinct().Count(),
                    InvalidRows = errors.Select(e => e.Row).Distinct().Count(),
                    ErrorCount = errors.Count,
                    Errors = errors.OrderBy(e => e.Row).Take(500).ToList()
                });
            }

            var result = new CatalogImportCommitResult(sha256, analysis.Rows.Count, totals.CreatedProducts, totals.CreatedVariants,
                totals.UpdatedProducts, totals.UpdatedVariants, totals.UnchangedProducts, totals.UnchangedVariants);
            db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), currentUser.UserId, currentUser.IpAddress, "catalog.import.committed",
                "catalog_import", sha256, null, JsonSerializer.Serialize(result), currentUser.CorrelationId, DateTime.UtcNow));
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new CatalogImportCommitOutcome(result, null);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("CONCURRENT_MODIFICATION", "The catalogue changed during the import. Nothing was written; retry.");
        }
    }

    private async Task WriteAsync(IReadOnlyList<CatalogImportRow> rows, Guid sellerId, Totals totals, List<CatalogImportRowError> errors, CancellationToken ct)
    {
        var brands = (await db.Brands.AsNoTracking().Select(b => new { b.Id, b.Name, b.Slug }).ToListAsync(ct))
            .SelectMany(b => new[] { (b.Slug, b.Id), (b.Name, b.Id) }).DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Item1, x => x.Id, StringComparer.OrdinalIgnoreCase);
        var categories = (await db.Categories.AsNoTracking().Select(c => new { c.Id, c.Slug, c.MaterializedPath }).ToListAsync(ct))
            .SelectMany(c => new[] { (c.Slug, c.Id), (c.MaterializedPath, c.Id) }).DistinctBy(x => x.Item1, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(x => x.Item1, x => x.Id, StringComparer.OrdinalIgnoreCase);

        var codes = rows.SelectMany(r => r.Attributes.Keys).Distinct().ToList();
        var definitions = codes.Count == 0 ? []
            : await db.Attributes.Include(a => a.Values).Where(a => codes.Contains(a.Code)).ToDictionaryAsync(a => a.Code, StringComparer.Ordinal, ct);

        foreach (var batch in rows.GroupBy(r => r.Slug).Chunk(BatchProducts))
        {
            var slugs = batch.Select(g => g.Key).ToList();
            var existing = (await db.Products.Include(p => p.Variants).Include(p => p.AttributeValues)
                    .Where(p => slugs.Contains(p.Slug) && p.DeletedAtUtc == null).ToListAsync(ct))
                .ToDictionary(p => p.Slug, StringComparer.Ordinal);

            foreach (var group in batch)
            {
                try
                {
                    await ApplyProductAsync([.. group], existing.GetValueOrDefault(group.Key), sellerId, brands, categories, definitions, totals, ct);
                }
                catch (DomainException ex)
                {
                    string message = ex is ValidationException { Errors.Count: > 0 } v ? v.Errors[0].Message : ex.Message;
                    errors.Add(new CatalogImportRowError(group.First().Row, "", "DOMAIN_RULE", message));
                }
            }

            if (errors.Count > 0) return;
            await db.SaveChangesAsync(ct);
            // Products are not needed again; detach them (and the rows the interceptor queued) so a 20,000-row file stays small.
            foreach (var e in db.ChangeTracker.Entries().Where(e => e.Entity is Product or ProductVariant or ProductAttributeValue or AuditLog or OutboxMessage).ToList())
                e.State = EntityState.Detached;
        }
    }

    private async Task ApplyProductAsync(
        List<CatalogImportRow> group, Product? product, Guid sellerId, Dictionary<string, Guid> brands, Dictionary<string, Guid> categories,
        Dictionary<string, AttributeDefinition> definitions, Totals totals, CancellationToken ct)
    {
        var first = group[0];
        Guid categoryId = categories[first.Category];
        Guid? brandId = first.Brand is null ? null : brands[first.Brand];
        var supply = group.Select(r => r.SupplyStatus).FirstOrDefault(s => s is not null);
        var datasheet = group.Select(r => r.DatasheetUrl).FirstOrDefault(u => u is not null);
        bool isNew = product is null;
        bool productChanged = false, variantsChanged = false;

        if (product is null)
        {
            product = new Product(Guid.NewGuid(), sellerId, categoryId, brandId, first.TitleEn, first.TitleBn, first.Slug, "", "", "");
            if (datasheet is not null) product.SetContent("[]", "{}", datasheet, null);
            if (supply is { } s && s != SupplyStatus.Active) product.SetSupply(s, null);
        }
        else
        {
            if (product.SellerId != sellerId) throw new ValidationException("sellerId", $"The product '{first.Slug}' belongs to another seller.");
            if (product.TitleEn != first.TitleEn || product.TitleBn != first.TitleBn) { product.SetTitles(first.TitleEn, first.TitleBn); productChanged = true; }
            if (product.CategoryId != categoryId || (brandId is not null && product.BrandId != brandId))
            {
                product.Recategorize(categoryId, brandId ?? product.BrandId);
                productChanged = true;
            }

            if (datasheet is not null && product.DatasheetUrl != datasheet)
            {
                product.SetContent(product.HighlightsJson, product.SpecsJson, datasheet, product.CadModelUrl);
                productChanged = true;
            }

            if (supply is { } wanted && wanted != product.SupplyStatus)
            {
                // A phased-out product keeps its successor; back in supply it has none (the domain clears it).
                var successor = wanted != SupplyStatus.Active && product.SuccessorProductId is { } sid
                    ? await db.Products.FirstOrDefaultAsync(p => p.Id == sid, ct) : null;
                product.SetSupply(wanted, successor);
                productChanged = true;
            }
        }

        var attributes = group.SelectMany(r => r.Attributes).GroupBy(a => a.Key).ToDictionary(g => g.Key, g => g.First().Value);
        productChanged |= ApplyAttributes(product, attributes, definitions, isNew);

        foreach (var row in group)
        {
            var variant = product.Variants.FirstOrDefault(v => v.PartNumberKey == row.PartKey || v.Sku == row.Sku);
            var price = Money.FromMinorUnits(row.PriceMinor, Currency.BDT);
            if (variant is null)
            {
                variant = product.AddVariant(Guid.NewGuid(), row.Sku, null, new Dictionary<string, string> { ["Model"] = row.PartNumber },
                    price, null, null, 0.5m, false);
                variant.SetManufacturerPartNumber(row.PartNumber);
                if (row.MinOrderQty is not null || row.PackSize is not null)
                    product.SetOrderUnits(variant.Id, row.MinOrderQty ?? 1, row.PackSize ?? 1);
                if (!isNew) db.ProductVariants.Add(variant);
                totals.CreatedVariants++;
                variantsChanged = true;
                continue;
            }

            bool changed = false;
            if (variant.ManufacturerPartNumber != row.PartNumber)
            {
                product.SetManufacturerPartNumber(variant.Id, row.PartNumber);
                changed = true;
            }

            if (variant.PriceMinor != row.PriceMinor)
            {
                variant.ChangePrice(price, variant.CompareAtPrice);
                product.RequestReindex();   // a price change raises no product event by itself
                changed = true;
            }

            int moq = row.MinOrderQty ?? variant.MinOrderQty, pack = row.PackSize ?? variant.PackSize;
            if (moq != variant.MinOrderQty || pack != variant.PackSize)
            {
                product.SetOrderUnits(variant.Id, moq, pack);
                changed = true;
            }

            if (changed) { totals.UpdatedVariants++; variantsChanged = true; }
            else totals.UnchangedVariants++;
        }

        if (isNew)
        {
            db.Products.Add(product);
            totals.CreatedProducts++;
        }
        else if (productChanged || variantsChanged) totals.UpdatedProducts++;
        else totals.UnchangedProducts++;
    }

    /// <summary>Sets the product's value of each attribute in the file; returns whether anything changed.</summary>
    private bool ApplyAttributes(Product product, Dictionary<string, string> wanted, Dictionary<string, AttributeDefinition> definitions, bool isNew)
    {
        bool changed = false;
        foreach (var (code, text) in wanted)
        {
            var definition = definitions[code];
            var value = FindOrCreateValue(definition, text);
            var ownIds = definition.Values.Select(v => v.Id).ToHashSet();
            var current = product.AttributeValues.Where(a => ownIds.Contains(a.AttributeValueId)).ToList();
            if (current.Count == 1 && current[0].AttributeValueId == value.Id) continue;

            foreach (var old in current)
                if (product.RemoveAttributeValue(old.AttributeValueId) is { } link && !isNew) db.ProductAttributeValues.Remove(link);
            product.AssignAttributeValue(value.Id);
            if (!isNew) db.ProductAttributeValues.Add(product.AttributeValues.First(a => a.AttributeValueId == value.Id));
            changed = true;
        }

        return changed;
    }

    /// <summary>The attribute's existing value for this text (a number by its converted value, so "6 kA" finds "6000 A"), or a new one.</summary>
    private AttributeValue FindOrCreateValue(AttributeDefinition definition, string text)
    {
        string value = definition.DataType == AttributeDataType.Boolean
            ? (text.ToLowerInvariant() is "true" or "yes" or "1" ? "true" : "false") : text;

        AttributeValue? found;
        if (definition.DataType is AttributeDataType.Number or AttributeDataType.Range)
        {
            var (min, max) = UnitCatalog.Parse(text, definition.Unit!, allowRange: definition.DataType == AttributeDataType.Range);
            found = definition.Values.FirstOrDefault(v => v.NumericValue == min && v.NumericMax == max);
        }
        else found = definition.Values.FirstOrDefault(v => string.Equals(v.ValueEn, value, StringComparison.OrdinalIgnoreCase));

        if (found is not null) return found;
        var created = definition.AddValue(Guid.NewGuid(), value, value, definition.Values.Count == 0 ? 0 : definition.Values.Max(v => v.SortOrder) + 1);
        db.AttributeValues.Add(created);
        return created;
    }
}
