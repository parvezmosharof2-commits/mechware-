namespace Mechware.Application.Catalog;

/// <summary>
/// One variant as staff see it when picking it for a price list (E8.3b). No personal data. <see cref="PriceMinor"/> is the list
/// (retail, VAT-inclusive) price; <see cref="CostMinor"/> is null unless the caller may see cost.
/// </summary>
public sealed record AdminVariantDto(
    Guid VariantId, Guid ProductId, string Sku, string? PartNumber, string Title, string? Brand, string ProductStatus, bool Active,
    long PriceMinor, Guid SellerId, int MinOrderQty, int PackSize, [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)] long? CostMinor);

public interface IAdminVariantLookup
{
    public const int MaxPageSize = 50;

    /// <summary>
    /// Staff lookup by exact SKU (case-insensitive) or free text over SKU, manufacturer part number and title, drafts and
    /// unpublished products included; soft-deleted products never. Cost is returned only when <paramref name="includeCost"/>.
    /// </summary>
    Task<PagedResult<AdminVariantDto>> SearchAsync(string? sku, string? q, int page, int pageSize, bool includeCost, CancellationToken ct = default);
}
