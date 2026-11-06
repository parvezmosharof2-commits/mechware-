using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Catalog;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Catalog;

/// <summary>
/// Staff edit a product's product-information data (sprint 3–4): supply status and successor, alternatives, and the order
/// units of each SKU. The rules live in the domain (<see cref="Product"/>); this loads, applies and saves. Saving raises the
/// product's update event, whose relay evicts the cached product page and the pages that point to it.
/// </summary>
public sealed class CatalogAdminService(ShopVerseDbContext db) : ICatalogAdminService
{
    public async Task<ProductPimDto> GetPimAsync(Guid productId, CancellationToken ct = default) =>
        await ToDtoAsync(await LoadAsync(productId, ct), ct);

    public async Task<ProductPimDto> SetSupplyAsync(Guid productId, SetSupplyRequest request, CancellationToken ct = default)
    {
        var product = await LoadAsync(productId, ct);
        var status = ParseStatus(request.SupplyStatus);

        Product? successor = null;
        if (request.SuccessorProductId is { } successorId)
        {
            successor = await db.Products.FirstOrDefaultAsync(p => p.Id == successorId && p.DeletedAtUtc == null, ct)
                        ?? throw new ValidationException("successorProductId", "The successor product does not exist.");
        }

        product.SetSupply(status, successor, request.SuccessorIsDropIn, request.SuccessorNote);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(product, ct);
    }

    public async Task<ProductPimDto> SetAlternativesAsync(Guid productId, SetAlternativesRequest request, CancellationToken ct = default)
    {
        var product = await LoadAsync(productId, ct);
        var ids = request.AlternativeProductIds ?? throw new ValidationException("alternativeProductIds", "The list of alternatives is required (it may be empty).");
        // Checked before the lookup: a huge list must not become a huge query.
        if (ids.Count > Product.MaxAlternatives)
            throw new ValidationException("alternativeProductIds", $"A product can list at most {Product.MaxAlternatives} alternatives.");

        var found = await db.Products.Where(p => ids.Contains(p.Id) && p.DeletedAtUtc == null).ToDictionaryAsync(p => p.Id, ct);
        var missing = ids.Where(id => !found.ContainsKey(id)).ToList();
        if (missing.Count > 0)
            throw new ValidationException("alternativeProductIds", $"These products do not exist: {string.Join(", ", missing)}.");

        product.ReplaceAlternatives([.. ids.Select(id => found[id])]);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(product, ct);
    }

    public async Task<ProductPimDto> SetOrderUnitsAsync(Guid productId, Guid variantId, SetOrderUnitsRequest request, CancellationToken ct = default)
    {
        var product = await LoadAsync(productId, ct);
        product.SetOrderUnits(variantId, request.MinOrderQty, request.PackSize);
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(product, ct);
    }

    private async Task<Product> LoadAsync(Guid productId, CancellationToken ct) =>
        await db.Products.Include(p => p.Variants).Include(p => p.Alternatives)
            .FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAtUtc == null, ct)
        ?? throw new KeyNotFoundException("The product does not exist.");

    private static SupplyStatus ParseStatus(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "active" => SupplyStatus.Active,
        "nrnd" => SupplyStatus.Nrnd,
        "obsolete" => SupplyStatus.Obsolete,
        _ => throw new ValidationException("supplyStatus", "The supply status must be active, nrnd or obsolete.")
    };

    private static string Name(SupplyStatus s) => s switch
    {
        SupplyStatus.Nrnd => "nrnd",
        SupplyStatus.Obsolete => "obsolete",
        _ => "active"
    };

    private static string Name(ProductStatus s) => s.ToString().ToLowerInvariant();

    private async Task<ProductPimDto> ToDtoAsync(Product product, CancellationToken ct)
    {
        var linkedIds = product.Alternatives.Select(a => a.AlternativeProductId)
            .Concat(product.SuccessorProductId is { } s ? [s] : []).Distinct().ToList();
        var linked = linkedIds.Count == 0 ? []
            : await db.Products.AsNoTracking().Where(p => linkedIds.Contains(p.Id))
                .Select(p => new { p.Id, p.Slug, p.TitleEn, p.Status, p.SupplyStatus, p.DeletedAtUtc }).ToDictionaryAsync(p => p.Id, ct);

        AdminSuccessorDto? successor = product.SuccessorProductId is { } sid && linked.TryGetValue(sid, out var sp)
            ? new AdminSuccessorDto(sid, sp.Slug, sp.TitleEn, Name(sp.Status), product.SuccessorIsDropIn, product.SuccessorNote)
            : null;

        var alternatives = product.Alternatives.OrderBy(a => a.Position)
            .Where(a => linked.ContainsKey(a.AlternativeProductId))
            .Select(a =>
            {
                var p = linked[a.AlternativeProductId];
                bool listed = p.Status == ProductStatus.Active && p.DeletedAtUtc == null && p.SupplyStatus != SupplyStatus.Obsolete;
                return new AdminAlternativeDto(a.AlternativeProductId, p.Slug, p.TitleEn, Name(p.Status), Name(p.SupplyStatus), listed);
            }).ToList();

        var variants = product.Variants.OrderByDescending(v => v.IsDefault).ThenBy(v => v.Sku, StringComparer.Ordinal)
            .Select(v => new AdminVariantUnitsDto(v.Id, v.Sku, v.MinOrderQty, v.PackSize)).ToList();

        return new ProductPimDto(product.Id, product.Slug, product.TitleEn, Name(product.Status), Name(product.SupplyStatus),
            successor, alternatives, variants);
    }
}
