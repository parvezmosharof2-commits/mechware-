using Microsoft.EntityFrameworkCore;
using Mechware.Application.Catalog;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Catalog;

/// <summary>Read-only look-ups for the import dry run: no tracking, no writes.</summary>
public sealed class CatalogImportLookup(MechwareDbContext db) : ICatalogImportLookup
{
    public async Task<IReadOnlyList<ImportBrandRef>> BrandsAsync(CancellationToken ct) =>
        await db.Brands.AsNoTracking().Select(b => new ImportBrandRef(b.Name, b.Slug)).ToListAsync(ct);

    public async Task<IReadOnlyList<ImportCategoryRef>> CategoriesAsync(CancellationToken ct) =>
        await db.Categories.AsNoTracking().Select(c => new ImportCategoryRef(c.Slug, c.MaterializedPath)).ToListAsync(ct);

    public async Task<IReadOnlyList<ImportAttributeRef>> AttributesAsync(CancellationToken ct) =>
        await db.Attributes.AsNoTracking().Select(a => new ImportAttributeRef(a.Code, a.DataType, a.Unit)).ToListAsync(ct);

    public async Task<IReadOnlySet<string>> ExistingProductSlugsAsync(IReadOnlyCollection<string> slugs, CancellationToken ct)
    {
        var list = slugs.ToArray();
        return (await db.Products.AsNoTracking().Where(p => list.Contains(p.Slug)).Select(p => p.Slug).ToListAsync(ct)).ToHashSet();
    }

    public async Task<IReadOnlyList<ImportVariantRef>> ExistingVariantsAsync(
        IReadOnlyCollection<string> partKeys, IReadOnlyCollection<string> skus, CancellationToken ct)
    {
        var keys = partKeys.ToArray();
        var skuList = skus.ToArray();
        return await (from v in db.ProductVariants.AsNoTracking()
                      join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                      where skuList.Contains(v.Sku) || (v.PartNumberKey != null && keys.Contains(v.PartNumberKey))
                      select new ImportVariantRef(v.Sku, v.PartNumberKey, p.Slug)).ToListAsync(ct);
    }
}
