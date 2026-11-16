namespace Mechware.Application.Pricing;

/// <summary>
/// Upsert one margin floor row by its key (scope, brand, category); ADR-0008 §4, D2.9. <paramref name="Scope"/> is "Global",
/// "Brand", "Category" or "BrandAndCategory"; Global takes no ids, Brand only a brand, Category only a category,
/// BrandAndCategory both. <paramref name="MinMarkupPercent"/> is 0 to 1,000 with at most two decimals (a third decimal is
/// refused, never rounded). There is no author member: the author is the signed-in user.
/// </summary>
public sealed record UpsertMarginFloorRequest(string? Scope, Guid? BrandId, Guid? CategoryId, decimal? MinMarkupPercent);

/// <summary>One stored floor row. Markups only, never a cost or an amount.</summary>
public sealed record MarginFloorDto
{
    public Guid Id { get; init; }
    public string Scope { get; init; } = string.Empty;
    public Guid? BrandId { get; init; }
    public string? BrandName { get; init; }
    public Guid? CategoryId { get; init; }
    public string? CategoryName { get; init; }

    /// <summary>English category names from the root down to the row's category; empty without a category.</summary>
    public IReadOnlyList<string> CategoryPath { get; init; } = [];

    public decimal MinMarkupPercent { get; init; }

    /// <summary>Null for the row the migration seeded.</summary>
    public Guid? UpdatedBy { get; init; }

    public string? UpdatedByName { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
}

/// <summary>
/// The floor row that applies to a variant (or a brand and/or category) by the policy's precedence: brand + category (own,
/// then ancestors) → brand → category (own, then ancestors) → global. <see cref="Floor"/> is null only when there is no
/// global row, which the policy treats as "every entry is below floor" (<see cref="FailClosed"/>).
/// </summary>
public sealed record EffectiveMarginFloorDto
{
    public Guid? VariantId { get; init; }
    public Guid? BrandId { get; init; }
    public Guid? CategoryId { get; init; }

    /// <summary>The category and its ancestors, nearest first, as the policy walks them.</summary>
    public IReadOnlyList<Guid> CategoryAncestry { get; init; } = [];

    public MarginFloorDto? Floor { get; init; }
    public decimal? MinMarkupPercent { get; init; }
    public bool FailClosed { get; init; }
}

/// <summary>Staff margin floor administration (D2.9).</summary>
public interface IMarginFloorService
{
    Task<IReadOnlyList<MarginFloorDto>> ListAsync(CancellationToken ct = default);

    /// <summary>Either <paramref name="variantId"/>, or a brand and/or a category.</summary>
    Task<EffectiveMarginFloorDto> EffectiveAsync(Guid? variantId, Guid? brandId, Guid? categoryId, CancellationToken ct = default);

    /// <summary>Creates the row for this key or changes its markup; serialised with every other floor change; audited old → new.</summary>
    Task<MarginFloorDto> UpsertAsync(UpsertMarginFloorRequest request, CancellationToken ct = default);

    /// <summary>Deletes a non-global row; the global row is required (409 <c>GLOBAL_FLOOR_REQUIRED</c>).</summary>
    Task DeleteAsync(Guid id, CancellationToken ct = default);
}
