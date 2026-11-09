namespace ShopVerse.Application.Catalog;

/// <summary>Where one product stands in the product-information data, as staff see it (drafts and archived products too).</summary>
public sealed record ProductPimDto(
    Guid Id,
    string Slug,
    string TitleEn,
    string Status,
    string SupplyStatus,
    AdminSuccessorDto? Successor,
    IReadOnlyList<AdminAlternativeDto> Alternatives,
    IReadOnlyList<AdminVariantUnitsDto> Variants);

public sealed record AdminSuccessorDto(Guid ProductId, string Slug, string TitleEn, string Status, bool IsDropIn, string? Note);

/// <summary><c>Listed</c> is false when buyers do not see it (it is no longer published, or has gone obsolete); staff should replace it.</summary>
public sealed record AdminAlternativeDto(Guid ProductId, string Slug, string TitleEn, string Status, string SupplyStatus, bool Listed);

public sealed record AdminVariantUnitsDto(Guid VariantId, string Sku, int MinOrderQty, int PackSize, string? ManufacturerPartNumber);

/// <summary>
/// <c>SupplyStatus</c>: <c>active</c>, <c>nrnd</c> or <c>obsolete</c>. <c>active</c> has no successor. For the other two, name the
/// successor, or send it again: a call that leaves it out while the product has one is refused, so a status change cannot drop a
/// successor unnoticed. To remove it on purpose, send <c>ClearSuccessor: true</c>.
/// Leave <c>SuccessorIsDropIn</c> and <c>SuccessorNote</c> out to keep what is stored while the successor stays the same one;
/// a blank note clears it.
/// </summary>
public sealed record SetSupplyRequest(
    string? SupplyStatus, Guid? SuccessorProductId, bool? SuccessorIsDropIn, string? SuccessorNote, bool? ClearSuccessor = null);

/// <summary>The complete list of alternatives, in display order.</summary>
public sealed record SetAlternativesRequest(IReadOnlyList<Guid>? AlternativeProductIds);

public sealed record SetOrderUnitsRequest(int MinOrderQty, int PackSize);

/// <summary>The manufacturer part number as printed; null or blank clears it.</summary>
public sealed record SetPartNumberRequest(string? ManufacturerPartNumber);

public interface ICatalogAdminService
{
    Task<ProductPimDto> GetPimAsync(Guid productId, CancellationToken ct = default);
    Task<ProductPimDto> SetSupplyAsync(Guid productId, SetSupplyRequest request, CancellationToken ct = default);
    Task<ProductPimDto> SetAlternativesAsync(Guid productId, SetAlternativesRequest request, CancellationToken ct = default);
    Task<ProductPimDto> SetOrderUnitsAsync(Guid productId, Guid variantId, SetOrderUnitsRequest request, CancellationToken ct = default);
    Task<ProductPimDto> SetPartNumberAsync(Guid productId, Guid variantId, SetPartNumberRequest request, CancellationToken ct = default);
}
