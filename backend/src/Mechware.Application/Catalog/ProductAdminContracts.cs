namespace Mechware.Application.Catalog;

/// <summary>One row of the staff product list. Prices are the catalogue's VAT-inclusive list prices in poisha.</summary>
public sealed record ProductListItemDto(
    Guid Id, string Slug, string TitleEn, string TitleBn,
    Guid? BrandId, string? BrandName,
    Guid CategoryId, string CategoryName, string CategoryPath,
    Guid SellerId, string SellerStoreName,
    string Status, string SupplyStatus, int VariantCount, long? MinPriceMinor, long? MaxPriceMinor,
    DateTime? UpdatedAtUtc, long RowVersion);

/// <summary>The product as the edit form needs it. <c>RowVersion</c> is what an update sends back as <c>expectedVersion</c>.</summary>
public sealed record ProductAdminDetailDto(
    Guid Id, string Slug, string TitleEn, string TitleBn,
    string ShortDescriptionEn, string ShortDescriptionBn, string DescriptionHtml,
    Guid? BrandId, string? BrandName, Guid CategoryId, string CategoryName, string CategoryPath,
    Guid SellerId, string SellerStoreName,
    string Status, IReadOnlyList<string> AllowedTransitions, string? RejectionReason, string SupplyStatus,
    decimal LengthCm, decimal WidthCm, decimal HeightCm,
    DateTime? UpdatedAtUtc, long RowVersion,
    IReadOnlyList<ProductAdminVariantDto> Variants);

public sealed record ProductAdminVariantDto(
    Guid Id, string Sku, bool IsDefault, string Status, decimal WeightKg, long PriceMinor, long? CompareAtPriceMinor,
    long? SalePriceMinor, DateTime? SaleStartsAtUtc, DateTime? SaleEndsAtUtc, int MinOrderQty, int PackSize, string? ManufacturerPartNumber);

/// <summary>
/// Whole-form update of the editorial fields (PUT): every field is sent, a null <c>BrandId</c> means no brand. The slug is not
/// editable here. The dimensions are optional (null keeps them). <c>DescriptionHtml</c> must already be inside the allowed markup.
/// </summary>
public sealed record UpdateProductRequest(
    string? TitleEn, string? TitleBn, string? ShortDescriptionEn, string? ShortDescriptionBn, string? DescriptionHtml,
    Guid? BrandId, Guid? CategoryId, decimal? LengthCm, decimal? WidthCm, decimal? HeightCm, long? ExpectedVersion);

/// <summary>A null field is left as it is. <c>Status</c> is <c>active</c> or <c>inactive</c>. A price change needs <c>ExpectedVersion</c> too.</summary>
public sealed record UpdateVariantRequest(decimal? WeightKg, long? PriceMinor, string? Status, long? ExpectedVersion);

public sealed record ProductLifecycleRequest(long? ExpectedVersion);

public interface IProductAdminService
{
    public const int MaxPageSize = 50;

    Task<PagedResult<ProductListItemDto>> ListAsync(string? q, string? status, Guid? brandId, Guid? categoryId, string? supplyStatus,
        Guid? sellerId, int page, int pageSize, CancellationToken ct = default);

    Task<ProductAdminDetailDto> GetAsync(Guid productId, CancellationToken ct = default);
    Task<ProductAdminDetailDto> UpdateAsync(Guid productId, UpdateProductRequest request, CancellationToken ct = default);
    Task<ProductAdminDetailDto> UpdateVariantAsync(Guid productId, Guid variantId, UpdateVariantRequest request, CancellationToken ct = default);

    /// <summary><paramref name="action"/> is <c>publish</c>, <c>archive</c> or <c>restore</c>.</summary>
    Task<ProductAdminDetailDto> TransitionAsync(Guid productId, string action, ProductLifecycleRequest? request, CancellationToken ct = default);
}
