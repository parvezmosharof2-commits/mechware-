namespace Mechware.Application.Catalog;

/// <summary>A brand as staff pick it (margin floors). The brand table has no Bangla name and no active flag yet, so <see cref="NameBn"/> is null and <see cref="IsActive"/> is true.</summary>
public sealed record AdminBrandDto(Guid Id, string NameEn, string? NameBn, string Slug, bool IsActive);

/// <summary>A category as staff pick it. <see cref="Path"/> is the English names from the root down to and including this node.</summary>
public sealed record AdminCategoryDto(Guid Id, string NameEn, string? NameBn, string Slug, Guid? ParentId, IReadOnlyList<string> Path, bool IsActive);

public interface IAdminCatalogLookup
{
    public const int MaxPageSize = 50;

    /// <summary>Staff brand lookup: <paramref name="q"/> is a case-insensitive "contains" on name and slug (LIKE wildcards literal); sorted by name.</summary>
    Task<PagedResult<AdminBrandDto>> SearchBrandsAsync(string? q, int page, int pageSize, CancellationToken ct = default);

    /// <summary>Staff category lookup: <paramref name="q"/> over English name, Bangla name and slug; sorted by English name; paths from one tree query.</summary>
    Task<PagedResult<AdminCategoryDto>> SearchCategoriesAsync(string? q, int page, int pageSize, CancellationToken ct = default);
}
