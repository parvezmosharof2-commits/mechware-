using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Configurations;

namespace Mechware.Infrastructure.Catalog;

/// <summary>
/// Staff product management (C1.7): list, read, edit and lifecycle. The rules stay in <see cref="Product"/>; this class adds the
/// input limits, the existence checks, an <c>xmin</c>-based version (a stale edit is a 409, never a silent overwrite), the audit
/// row for a price change, and the mapping of lifecycle refusals to 409. Every change goes through the aggregate, so it raises
/// <see cref="ProductUpdatedEvent"/> (search reindex and cache eviction through the outbox). The slug is never touched.
/// </summary>
public sealed class ProductAdminService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock) : IProductAdminService
{
    public const int MaxTitleLength = 255;
    public const int MaxShortDescriptionLength = 1000;
    public const int MaxDescriptionHtmlLength = 20_000;
    private const decimal MaxDimensionCm = 1000m;

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Sign in to manage products.");

    public async Task<PagedResult<ProductListItemDto>> ListAsync(string? q, string? status, Guid? brandId, Guid? categoryId,
        string? supplyStatus, Guid? sellerId, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, IProductAdminService.MaxPageSize);

        var query = from p in db.Products.AsNoTracking()
                    where p.DeletedAtUtc == null
                    join s in db.Sellers.AsNoTracking() on p.SellerId equals s.Id
                    join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                    join b in db.Brands.AsNoTracking() on p.BrandId equals b.Id into brands
                    from b in brands.DefaultIfEmpty()
                    select new { p, Store = s.StoreName, CategoryName = c.NameEn, c.MaterializedPath, BrandName = b == null ? null : b.Name };

        if (!string.IsNullOrWhiteSpace(status))
        {
            var wanted = ParseEnum<ProductStatus>(status, "status");
            query = query.Where(x => x.p.Status == wanted);
        }

        if (!string.IsNullOrWhiteSpace(supplyStatus))
        {
            var wanted = ParseEnum<SupplyStatus>(supplyStatus, "supplyStatus");
            query = query.Where(x => x.p.SupplyStatus == wanted);
        }

        if (brandId is { } brand) query = query.Where(x => x.p.BrandId == brand);
        if (categoryId is { } category) query = query.Where(x => x.p.CategoryId == category);
        if (sellerId is { } seller) query = query.Where(x => x.p.SellerId == seller);

        if (q?.Trim() is { Length: > 0 } term)
        {
            // Literal contains, case-insensitive: LIKE wildcards typed by the caller are escaped.
            var like = "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(x => EF.Functions.ILike(x.p.TitleEn, like, "\\") || EF.Functions.ILike(x.p.TitleBn, like, "\\")
                || EF.Functions.ILike(x.p.Slug, like, "\\")
                || db.ProductVariants.Any(v => v.ProductId == x.p.Id
                    && (EF.Functions.ILike(v.Sku, like, "\\") || (v.ManufacturerPartNumber != null && EF.Functions.ILike(v.ManufacturerPartNumber, like, "\\")))));
        }

        var total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(x => x.p.UpdatedAtUtc ?? x.p.CreatedAtUtc).ThenBy(x => x.p.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(x => new
            {
                x.p.Id,
                x.p.Slug,
                x.p.TitleEn,
                x.p.TitleBn,
                x.p.BrandId,
                x.BrandName,
                x.p.CategoryId,
                x.CategoryName,
                x.MaterializedPath,
                x.p.SellerId,
                x.Store,
                x.p.Status,
                x.p.SupplyStatus,
                x.p.UpdatedAtUtc,
                Version = EF.Property<uint>(x.p, "xmin"),
                // One aggregate per row, in the same statement: the number of queries does not grow with the page.
                Count = db.ProductVariants.Count(v => v.ProductId == x.p.Id),
                Min = db.ProductVariants.Where(v => v.ProductId == x.p.Id).Min(v => (long?)v.PriceMinor),
                Max = db.ProductVariants.Where(v => v.ProductId == x.p.Id).Max(v => (long?)v.PriceMinor)
            }).ToListAsync(ct);

        var items = rows.Select(r => new ProductListItemDto(r.Id, r.Slug, r.TitleEn, r.TitleBn, r.BrandId, r.BrandName, r.CategoryId,
            r.CategoryName, PathOf(r.MaterializedPath), r.SellerId, r.Store, Name(r.Status), Name(r.SupplyStatus), r.Count, r.Min, r.Max,
            r.UpdatedAtUtc, r.Version)).ToList();
        return new PagedResult<ProductListItemDto>(items, page, pageSize, total);
    }

    public async Task<ProductAdminDetailDto> GetAsync(Guid productId, CancellationToken ct = default) =>
        await DetailAsync(await LoadAsync(productId, tracked: false, ct), ct);

    public async Task<ProductAdminDetailDto> UpdateAsync(Guid productId, UpdateProductRequest request, CancellationToken ct = default)
    {
        var actor = Actor;
        var expected = RequireVersion(request.ExpectedVersion);
        var errors = new List<ValidationError>();
        var titleEn = Text(request.TitleEn, "titleEn", "English title", MaxTitleLength, required: true, errors);
        var titleBn = Text(request.TitleBn, "titleBn", "Bengali title", MaxTitleLength, required: true, errors);
        var shortEn = Text(request.ShortDescriptionEn, "shortDescriptionEn", "English short description", MaxShortDescriptionLength, false, errors);
        var shortBn = Text(request.ShortDescriptionBn, "shortDescriptionBn", "Bengali short description", MaxShortDescriptionLength, false, errors);
        var html = request.DescriptionHtml ?? string.Empty;
        if (html.Length > MaxDescriptionHtmlLength)
            errors.Add(new("descriptionHtml", $"The description is at most {MaxDescriptionHtmlLength} characters."));
        foreach (var (field, value) in new[] { ("descriptionHtml", html), ("shortDescriptionEn", shortEn), ("shortDescriptionBn", shortBn) })
            foreach (var problem in RichTextSanitizer.FindProblems(value)) errors.Add(new(field, problem));
        if (request.CategoryId is null) errors.Add(new("categoryId", "A category is required."));
        foreach (var (field, v) in new[] { ("lengthCm", request.LengthCm), ("widthCm", request.WidthCm), ("heightCm", request.HeightCm) })
            if (v is { } d && (d <= 0 || d > MaxDimensionCm || decimal.Round(d, 2) != d))
                errors.Add(new(field, $"A dimension is above 0 and at most {MaxDimensionCm:0} cm, with at most 2 decimals."));
        if (errors.Count > 0) throw new ValidationException(errors);

        var product = await LoadAsync(productId, tracked: true, ct);
        CheckVersion(product, expected);

        var categoryId = request.CategoryId!.Value;
        if (!await db.Categories.AnyAsync(c => c.Id == categoryId, ct)) throw new ValidationException("categoryId", "The category does not exist.");
        if (request.BrandId is { } brandId && !await db.Brands.AnyAsync(b => b.Id == brandId, ct))
            throw new ValidationException("brandId", "The brand does not exist.");

        var before = Summary(product);
        product.SetTitles(titleEn, titleBn);
        product.SetDescription(shortEn, shortBn, html);
        if (product.CategoryId != categoryId || product.BrandId != request.BrandId) product.Recategorize(categoryId, request.BrandId);
        if (request.LengthCm is not null || request.WidthCm is not null || request.HeightCm is not null)
            product.SetLogistics(product.Condition, product.WarrantyMonths, product.ReturnPolicyDays, product.WeightKg,
                request.LengthCm ?? product.LengthCm, request.WidthCm ?? product.WidthCm, request.HeightCm ?? product.HeightCm);
        db.AuditLogs.Add(Audit("catalog.product.edited", "products", product.Id, before, Summary(product), actor));
        await SaveAsync(ct);
        return await DetailAsync(product, ct);
    }

    public async Task<ProductAdminDetailDto> UpdateVariantAsync(Guid productId, Guid variantId, UpdateVariantRequest request, CancellationToken ct = default)
    {
        var actor = Actor;
        var expected = RequireVersion(request.ExpectedVersion);
        if (request.WeightKg is null && request.PriceMinor is null && request.Status is null)
            throw new ValidationException("body", "Send at least one of weightKg, priceMinor or status.");
        if (request.PriceMinor is { } price && (price < 1 || price > PriceListLimits.MaxUnitPriceMinor))
            throw new ValidationException("priceMinor", $"The price is between 1 and {PriceListLimits.MaxUnitPriceMinor} poisha.");
        VariantStatus? status = request.Status is null ? null : request.Status.Trim().ToLowerInvariant() switch
        {
            "active" => VariantStatus.Active,
            "inactive" => VariantStatus.Inactive,
            _ => throw new ValidationException("status", "The status must be active or inactive.")
        };

        var product = await LoadAsync(productId, tracked: true, ct);
        CheckVersion(product, expected);
        var variant = product.Variants.FirstOrDefault(v => v.Id == variantId) ?? throw new KeyNotFoundException("The SKU does not exist on this product.");
        var (oldPrice, oldWeight, oldStatus) = (variant.PriceMinor, variant.WeightKg, variant.Status);

        product.UpdateVariant(variantId, request.WeightKg, request.PriceMinor is { } p ? Money.FromMinorUnits(p, Currency.BDT) : null, status);

        if (variant.PriceMinor != oldPrice)
            db.AuditLogs.Add(Audit("catalog.variant.price_changed", "product_variants", variantId,
                JsonSerializer.Serialize(new { sku = variant.Sku, priceMinor = oldPrice }),
                JsonSerializer.Serialize(new { sku = variant.Sku, priceMinor = variant.PriceMinor }), actor));
        if (variant.WeightKg != oldWeight || variant.Status != oldStatus)
            db.AuditLogs.Add(Audit("catalog.variant.updated", "product_variants", variantId,
                JsonSerializer.Serialize(new { weightKg = oldWeight, status = Name(oldStatus) }),
                JsonSerializer.Serialize(new { weightKg = variant.WeightKg, status = Name(variant.Status) }), actor));
        await SaveAsync(ct);
        return await DetailAsync(product, ct);
    }

    public async Task<ProductAdminDetailDto> TransitionAsync(Guid productId, string action, ProductLifecycleRequest? request, CancellationToken ct = default)
    {
        var actor = Actor;
        var expected = RequireVersion(request?.ExpectedVersion);
        var product = await LoadAsync(productId, tracked: true, ct);
        CheckVersion(product, expected);
        var from = product.Status;
        try
        {
            switch (action)
            {
                case "publish":
                    if (!product.Variants.Any(v => v.Status == VariantStatus.Active))
                        throw new ConflictException("NO_SELLABLE_VARIANT", "A product needs at least one active SKU to be published.");
                    product.Publish();
                    break;
                case "archive": product.Archive(); break;
                case "restore": product.ReturnToDraft(); break;
                default: throw new KeyNotFoundException("Unknown lifecycle action.");
            }
        }
        // The transition rules answer with a reason code; for staff it is a conflict with the product's state, not a malformed request.
        catch (InvalidProductTransitionException ex) { throw new ConflictException(ex.Code, ex.Message); }
        catch (ValidationException ex) when (ex.Errors.Count == 1 && ex.Errors[0].Field is "variants" or "media")
        {
            throw new ConflictException(ex.Errors[0].Field == "variants" ? "NO_VARIANT" : "NO_MEDIA", ex.Message);
        }

        db.AuditLogs.Add(Audit($"catalog.product.{action}", "products", product.Id,
            JsonSerializer.Serialize(new { status = Name(from) }), JsonSerializer.Serialize(new { status = Name(product.Status) }), actor));
        await SaveAsync(ct);
        return await DetailAsync(product, ct);
    }

    // ----- helpers -----

    private static long RequireVersion(long? version) =>
        version is { } v and >= 0 ? v : throw new ValidationException("expectedVersion", "Send the version you loaded (expectedVersion).");

    private uint CurrentVersion(Product product) => db.Entry(product).Property<uint>("xmin").CurrentValue;

    /// <summary>Refuses a stale edit up front, and makes the UPDATE itself conditional on the same version (a race in between).</summary>
    private void CheckVersion(Product product, long expected)
    {
        if (expected != CurrentVersion(product)) throw Stale();
        db.Entry(product).Property<uint>("xmin").OriginalValue = (uint)expected;
    }

    private static ConflictException Stale() =>
        new("PRODUCT_STALE", "This product was changed by someone else since you opened it. Reload it and apply your change again.");

    private async Task SaveAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateConcurrencyException) { throw Stale(); }
    }

    private async Task<Product> LoadAsync(Guid productId, bool tracked, CancellationToken ct)
    {
        var query = tracked ? db.Products : db.Products.AsNoTracking();
        return await query.Include(p => p.Variants).Include(p => p.Media).FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAtUtc == null, ct)
               ?? throw new KeyNotFoundException("The product does not exist.");
    }

    private async Task<ProductAdminDetailDto> DetailAsync(Product p, CancellationToken ct)
    {
        var category = await db.Categories.AsNoTracking().Where(c => c.Id == p.CategoryId).Select(c => new { c.NameEn, c.MaterializedPath }).SingleAsync(ct);
        var brandName = p.BrandId is { } b ? await db.Brands.AsNoTracking().Where(x => x.Id == b).Select(x => x.Name).FirstOrDefaultAsync(ct) : null;
        var store = await db.Sellers.AsNoTracking().Where(s => s.Id == p.SellerId).Select(s => s.StoreName).FirstOrDefaultAsync(ct) ?? string.Empty;
        uint version = db.Entry(p).State == EntityState.Detached
            ? await db.Products.AsNoTracking().Where(x => x.Id == p.Id).Select(x => EF.Property<uint>(x, "xmin")).SingleAsync(ct)
            : CurrentVersion(p);

        var variants = p.Variants.OrderByDescending(v => v.IsDefault).ThenBy(v => v.Sku, StringComparer.Ordinal)
            .Select(v => new ProductAdminVariantDto(v.Id, v.Sku, v.IsDefault, Name(v.Status), v.WeightKg, v.PriceMinor, v.CompareAtPriceMinor,
                v.SalePriceMinor, v.SaleStartsAtUtc, v.SaleEndsAtUtc, v.MinOrderQty, v.PackSize, v.ManufacturerPartNumber)).ToList();
        return new ProductAdminDetailDto(p.Id, p.Slug, p.TitleEn, p.TitleBn, p.ShortDescriptionEn, p.ShortDescriptionBn, p.DescriptionHtml,
            p.BrandId, brandName, p.CategoryId, category.NameEn, PathOf(category.MaterializedPath), p.SellerId, store,
            Name(p.Status), AllowedTransitions(p.Status), p.RejectionReason, Name(p.SupplyStatus), p.LengthCm, p.WidthCm, p.HeightCm,
            p.UpdatedAtUtc, version, variants);
    }

    private static IReadOnlyList<string> AllowedTransitions(ProductStatus s) => s switch
    {
        ProductStatus.Draft => ["publish", "archive"],
        ProductStatus.PendingReview => ["publish"],
        ProductStatus.Active => ["archive"],
        ProductStatus.Rejected => ["restore", "archive"],
        ProductStatus.Archived => ["restore"],
        _ => []
    };

    private static string Text(string? value, string field, string label, int max, bool required, List<ValidationError> errors)
    {
        var text = value?.Trim() ?? string.Empty;
        if (required && text.Length == 0) errors.Add(new(field, $"{label} is required."));
        else if (text.Length > max) errors.Add(new(field, $"{label} is at most {max} characters."));
        return text;
    }

    private static string PathOf(string materializedPath) =>
        materializedPath.StartsWith("root.", StringComparison.Ordinal) ? materializedPath[5..] : materializedPath;

    private static T ParseEnum<T>(string value, string field) where T : struct, Enum =>
        Enum.TryParse<T>(value.Trim().Replace("_", string.Empty), ignoreCase: true, out var parsed) && Enum.IsDefined(parsed)
            ? parsed : throw new ValidationException(field, $"Unknown {field}.");

    private static string Name(ProductStatus s) => SnakeCaseEnumConverter<ProductStatus>.ToSnake(s.ToString());
    private static string Name(SupplyStatus s) => SnakeCaseEnumConverter<SupplyStatus>.ToSnake(s.ToString());
    private static string Name(VariantStatus s) => s.ToString().ToLowerInvariant();

    private static string Summary(Product p) => JsonSerializer.Serialize(new
    {
        p.TitleEn,
        p.TitleBn,
        p.ShortDescriptionEn,
        p.ShortDescriptionBn,
        descriptionLength = p.DescriptionHtml.Length,
        p.BrandId,
        p.CategoryId,
        p.LengthCm,
        p.WidthCm,
        p.HeightCm
    });

    private AuditLog Audit(string action, string table, Guid id, string? oldValues, string? newValues, Guid actor) =>
        new(Guid.NewGuid(), actor, user.IpAddress, action, table, id.ToString(), oldValues, newValues, user.CorrelationId, clock.GetUtcNow().UtcDateTime);
}
