using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Pricing;
using Mechware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mechware.Infrastructure.Search;

/// <summary>
/// Builds <see cref="ProductSearchDocument"/>s from PostgreSQL. A product is indexable while it is Active and not deleted;
/// obsolete parts stay findable (last-time buy while in stock) and carry their supply status.
/// </summary>
public sealed class ProductSearchDocumentLoader(MechwareDbContext db, TimeProvider clock)
{
    /// <summary>Documents for the indexable products among <paramref name="productIds"/>; ids not returned must leave the index.</summary>
    public async Task<IReadOnlyList<ProductSearchDocument>> LoadAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return [];
        var products = await Indexable().Where(p => productIds.Contains(p.Id)).ToListAsync(ct);
        return await BuildAsync(products, ct);
    }

    /// <summary>One page of indexable products in id order (keyset paging), for a full rebuild.</summary>
    public async Task<IReadOnlyList<ProductSearchDocument>> LoadPageAsync(Guid? afterId, int take, CancellationToken ct = default)
    {
        var query = Indexable();
        if (afterId is { } after) query = query.Where(p => p.Id.CompareTo(after) > 0);
        var products = await query.OrderBy(p => p.Id).Take(take).ToListAsync(ct);
        return await BuildAsync(products, ct);
    }

    private IQueryable<Product> Indexable() => db.Products.AsNoTracking().AsSplitQuery()
        .Include(p => p.Variants).Include(p => p.AttributeValues)
        .Where(p => p.Status == ProductStatus.Active && p.DeletedAtUtc == null);

    private async Task<IReadOnlyList<ProductSearchDocument>> BuildAsync(List<Product> products, CancellationToken ct)
    {
        if (products.Count == 0) return [];

        var brandIds = products.Where(p => p.BrandId != null).Select(p => p.BrandId!.Value).Distinct().ToList();
        var brands = await db.Brands.AsNoTracking().Where(b => brandIds.Contains(b.Id))
            .ToDictionaryAsync(b => b.Id, b => (b.Name, b.Slug), ct);

        var categoryIds = products.Select(p => p.CategoryId).Distinct().ToList();
        var categories = await db.Categories.AsNoTracking().Where(c => categoryIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => (c.MaterializedPath, c.VatRate), ct);

        var variantIds = products.SelectMany(p => p.Variants).Where(v => v.Status == VariantStatus.Active).Select(v => v.Id).ToList();
        var stock = await db.InventoryItems.AsNoTracking()
            .Where(i => variantIds.Contains(i.VariantId))
            .Join(db.Warehouses.Where(w => w.IsActive), i => i.WarehouseId, w => w.Id, (i, _) => i)
            .GroupBy(i => i.VariantId)
            .Select(g => new { VariantId = g.Key, Available = g.Sum(i => i.OnHand - i.Reserved) })
            .ToDictionaryAsync(r => r.VariantId, r => r.Available, ct);

        var valueIds = products.SelectMany(p => p.AttributeValues).Select(a => a.AttributeValueId).Distinct().ToList();
        var numeric = await (from v in db.AttributeValues.AsNoTracking()
                             join d in db.Attributes.AsNoTracking() on v.AttributeId equals d.Id
                             where valueIds.Contains(v.Id) && v.NumericValue != null
                             select new { v.Id, d.Code, d.DataType, v.NumericValue, v.NumericMax })
            .ToDictionaryAsync(r => r.Id, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        var documents = new List<ProductSearchDocument>(products.Count);
        foreach (var p in products)
        {
            var variants = p.Variants.Where(v => v.Status == VariantStatus.Active).ToList();
            var (path, vatRate) = categories.TryGetValue(p.CategoryId, out var c) ? c : ("", 0m);
            var brand = p.BrandId is { } bid && brands.TryGetValue(bid, out var b) ? b : (Name: "", Slug: "");

            var attributes = new Dictionary<string, double>();
            foreach (var link in p.AttributeValues)
            {
                if (!numeric.TryGetValue(link.AttributeValueId, out var a)) continue;
                if (a.DataType == AttributeDataType.Range)
                {
                    attributes[a.Code + "_min"] = (double)a.NumericValue!.Value;
                    if (a.NumericMax is { } max) attributes[a.Code + "_max"] = (double)max;
                }
                else attributes[a.Code] = (double)a.NumericValue!.Value;
            }

            documents.Add(new ProductSearchDocument
            {
                Id = p.Id,
                Slug = p.Slug,
                TitleEn = p.TitleEn,
                TitleBn = p.TitleBn,
                BrandName = brand.Name,
                BrandSlug = brand.Slug,
                // "root.automation.plc" → ["automation", "plc"]: a filter on any ancestor finds the product.
                CategoryPath = path.Split('.', StringSplitOptions.RemoveEmptyEntries).Skip(1).ToList(),
                PartNumbers = variants.SelectMany(v => new[] { v.ManufacturerPartNumber, v.Sku })
                    .Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!).Distinct().ToList(),
                PartNumberKeys = variants.SelectMany(v => new[] { v.PartNumberKey, v.PartNumberFuzzyKey, PartNumbers.Normalise(v.Sku) })
                    .Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).Distinct().ToList(),
                // Guest-facing (VAT-inclusive) price at indexing time; a sale window that opens or closes later is picked up
                // by the next reindex of the product (or a full rebuild).
                PriceMinor = variants.Count == 0 ? 0
                    : variants.Min(v => PricingEngine.Quote(PricingInput.From(v, vatRate), now, TaxMode.Inclusive).Display.ToMinorUnits()),
                Currency = variants.FirstOrDefault()?.Currency.ToIsoCode() ?? "BDT",
                InStock = variants.Any(v => stock.TryGetValue(v.Id, out var available) && available > 0),
                SupplyStatus = p.SupplyStatus.ToString().ToLowerInvariant(),
                RatingAvg = (double)p.RatingAvg,
                SoldCount = p.SoldCount,
                NumericAttributes = attributes,
            });
        }
        return documents;
    }
}
