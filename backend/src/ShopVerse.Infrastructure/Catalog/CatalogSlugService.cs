using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Catalog;

/// <summary>
/// Slug renames with redirect bookkeeping. Handles the cases the bare aggregate cannot: reusing a retired slug
/// (A→B→A→C would otherwise violate the unique history index), refusing a slug another live entity owns, and
/// rewriting descendant category paths in the same transaction.
/// </summary>
public sealed class CatalogSlugService(ShopVerseDbContext db)
{
    public async Task ChangeProductSlugAsync(Guid productId, string newSlug, CancellationToken ct = default)
    {
        var slug = Slug.Require(newSlug);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var product = await db.Products.FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAtUtc == null, ct)
            ?? throw new KeyNotFoundException("Product not found.");
        if (product.Slug == slug) return;

        if (await db.Products.AnyAsync(p => p.Slug == slug && p.Id != productId && p.DeletedAtUtc == null, ct))
            throw new ConflictException("SLUG_IN_USE", $"The slug '{slug}' is already used by another product.");

        // A live slug always wins over history: drop any retired row that would shadow it (own or foreign).
        await DropRetiredAsync(SlugEntityType.Product, slug, ct);
        var retired = product.ChangeSlug(slug, Guid.NewGuid())!;
        await DropRetiredAsync(SlugEntityType.Product, retired.OldSlug, ct);
        db.SlugHistories.Add(retired);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task ChangeCategorySlugAsync(Guid categoryId, string newSlug, CancellationToken ct = default)
    {
        var slug = Slug.Require(newSlug);
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var category = await db.Categories.FirstOrDefaultAsync(c => c.Id == categoryId, ct)
            ?? throw new KeyNotFoundException("Category not found.");
        if (category.Slug == slug) return;

        if (await db.Categories.AnyAsync(c => c.Slug == slug && c.Id != categoryId, ct))
            throw new ConflictException("SLUG_IN_USE", $"The slug '{slug}' is already used by another category.");

        var oldPath = category.MaterializedPath;
        await DropRetiredAsync(SlugEntityType.Category, slug, ct);
        var retired = category.ChangeSlug(slug, Guid.NewGuid())!;
        await DropRetiredAsync(SlugEntityType.Category, retired.OldSlug, ct);
        db.SlugHistories.Add(retired);

        var prefix = oldPath + ".";
        var descendants = await db.Categories.Where(c => c.MaterializedPath.StartsWith(prefix)).ToListAsync(ct);
        foreach (var d in descendants) d.RebasePath(oldPath, category.MaterializedPath);

        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    private async Task DropRetiredAsync(SlugEntityType type, string slug, CancellationToken ct)
    {
        var stale = await db.SlugHistories.Where(h => h.EntityType == type && h.OldSlug == slug).ToListAsync(ct);
        db.SlugHistories.RemoveRange(stale);
        if (stale.Count > 0) await db.SaveChangesAsync(ct);
    }
}
