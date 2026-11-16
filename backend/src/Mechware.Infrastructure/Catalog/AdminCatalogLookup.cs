using Microsoft.EntityFrameworkCore;
using Mechware.Application.Catalog;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Catalog;

public sealed class AdminCatalogLookup(MechwareDbContext db) : IAdminCatalogLookup
{
    private static string? Like(string? q) =>
        q?.Trim() is { Length: > 0 } term ? "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%" : null;

    public async Task<PagedResult<AdminBrandDto>> SearchBrandsAsync(string? q, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, IAdminCatalogLookup.MaxPageSize);
        var query = db.Brands.AsNoTracking();
        if (Like(q) is { } like)
            query = query.Where(b => EF.Functions.ILike(b.Name, like, "\\") || EF.Functions.ILike(b.Slug, like, "\\"));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(b => b.Name).ThenBy(b => b.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(b => new { b.Id, b.Name, b.Slug }).ToListAsync(ct);
        return new PagedResult<AdminBrandDto>(rows.Select(b => new AdminBrandDto(b.Id, b.Name, null, b.Slug, true)).ToList(), page, pageSize, total);
    }

    public async Task<PagedResult<AdminCategoryDto>> SearchCategoriesAsync(string? q, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, IAdminCatalogLookup.MaxPageSize);
        var query = db.Categories.AsNoTracking();
        if (Like(q) is { } like)
            query = query.Where(c => EF.Functions.ILike(c.NameEn, like, "\\") || EF.Functions.ILike(c.NameBn, like, "\\") || EF.Functions.ILike(c.Slug, like, "\\"));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(c => c.NameEn).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize)
            .Select(c => new { c.Id, c.NameEn, c.NameBn, c.Slug, c.ParentId }).ToListAsync(ct);

        // The tree is small (three levels): one query gives every name, the paths are built in memory.
        var tree = rows.Count == 0 ? [] : await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId, c.NameEn }).ToDictionaryAsync(c => c.Id, ct);
        var items = rows.Select(r =>
        {
            var path = new List<string> { r.NameEn };
            var parent = r.ParentId;
            while (parent is { } id && tree.TryGetValue(id, out var node) && path.Count <= 8)
            {
                path.Insert(0, node.NameEn);
                parent = node.ParentId;
            }

            return new AdminCategoryDto(r.Id, r.NameEn, r.NameBn, r.Slug, r.ParentId, path, true);
        }).ToList();
        return new PagedResult<AdminCategoryDto>(items, page, pageSize, total);
    }
}
