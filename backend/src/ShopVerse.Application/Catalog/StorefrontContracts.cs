namespace ShopVerse.Application.Catalog;

/// <summary>Money in API form per ADR 0002.</summary>
public sealed record MoneyDto(string Amount, long AmountMinor, string Currency, string Formatted);

public sealed record BrandDto(string Name, string Slug, string? LogoUrl, bool IsVerified);
public sealed record SellerSummaryDto(string StoreName, string Slug, string? LogoUrl, decimal RatingAvg, int RatingCount);
public sealed record BreadcrumbDto(string NameEn, string NameBn, string Slug);
public sealed record MediaDto(string Url, string Alt, string Type, int Width, int Height, string? BlurDataUrl, Guid? VariantId);

/// <summary>Stock presentation: <c>in_stock</c>, <c>low_stock</c> or <c>out_of_stock</c>.</summary>
public sealed record StockDto(int Available, string Status);

public sealed record VariantPriceDto(
    MoneyDto Price, MoneyDto? CompareAt, int DiscountPercent, bool SaleActive, bool TaxIncluded, MoneyDto? Tax);

public sealed record VariantDto(
    Guid Id, string Sku, IReadOnlyDictionary<string, string> Options, bool IsDefault, VariantPriceDto Pricing, StockDto Stock);

public sealed record ProductDetailDto(
    Guid Id,
    string Slug,
    string TitleEn,
    string TitleBn,
    string ShortDescriptionEn,
    string ShortDescriptionBn,
    string DescriptionHtml,
    IReadOnlyList<string> Highlights,
    IReadOnlyDictionary<string, IReadOnlyDictionary<string, string>> Specs,
    BrandDto? Brand,
    SellerSummaryDto Seller,
    IReadOnlyList<BreadcrumbDto> Breadcrumbs,
    string Condition,
    int WarrantyMonths,
    int ReturnPolicyDays,
    decimal RatingAvg,
    int RatingCount,
    int SoldCount,
    string? DatasheetUrl,
    string? CadModelUrl,
    IReadOnlyList<MediaDto> Media,
    IReadOnlyList<VariantDto> Variants);

public sealed record ProductCardDto(
    Guid Id, string Slug, string TitleEn, string TitleBn, string? BrandName, string? ImageUrl, string? ImageAlt,
    MoneyDto Price, MoneyDto? CompareAt, int DiscountPercent, decimal RatingAvg, int RatingCount, string StockStatus);

public sealed record CategoryNodeDto(
    Guid Id, string NameEn, string NameBn, string Slug, string Path, int Depth, string? ImageUrl,
    IReadOnlyList<CategoryNodeDto> Children);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>Result of a slug lookup: found, permanently moved (301) or unknown.</summary>
public sealed record SlugLookupResult<T>(T? Value, string? RedirectToSlug)
{
    public bool IsFound => Value is not null;
    public bool IsRedirect => RedirectToSlug is not null;
    public static SlugLookupResult<T> Found(T value) => new(value, null);
    public static SlugLookupResult<T> MovedTo(string slug) => new(default, slug);
    public static SlugLookupResult<T> NotFound() => new(default, null);
}

public interface IStorefrontCatalogReader
{
    Task<SlugLookupResult<ProductDetailDto>> GetProductBySlugAsync(string slug, CancellationToken ct = default);
    Task<IReadOnlyList<CategoryNodeDto>> GetCategoryTreeAsync(CancellationToken ct = default);
    Task<SlugLookupResult<PagedResult<ProductCardDto>>> ListProductsByCategoryAsync(
        string categorySlug, int page, int pageSize, CancellationToken ct = default);
}

/// <summary>Cache invalidation hook driven by outbox events and inventory changes.</summary>
public interface IStorefrontCacheInvalidator
{
    Task InvalidateProductAsync(string slug, CancellationToken ct = default);
    Task InvalidateStockAsync(IEnumerable<Guid> productIds, CancellationToken ct = default);
    Task InvalidateCategoriesAsync(CancellationToken ct = default);
}
