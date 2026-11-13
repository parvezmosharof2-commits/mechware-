using Microsoft.EntityFrameworkCore;
using Mechware.Application.Cart;
using Mechware.Domain.Cart;
using Mechware.Domain.Catalog;
using Mechware.Domain.Pricing;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Cart;

/// <summary>
/// Cart lines priced from the catalog, read fresh from the database (not from the storefront cache, so a price or stock
/// change shows at once). Same rules as checkout: an active product that is not deleted, an active variant, the
/// <see cref="PricingEngine"/> quote in tax-inclusive (retail) mode as the unit price, and on-hand minus reserved over active
/// warehouses as availability.
/// </summary>
public sealed class CartCatalogReader(MechwareDbContext db, TimeProvider clock) : ICartCatalogReader
{
    public async Task<IReadOnlyDictionary<Guid, CartVariantView>> GetAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct = default)
    {
        if (variantIds.Count == 0) return new Dictionary<Guid, CartVariantView>();
        var ids = variantIds.Distinct().ToList();

        var rows = await (from v in db.ProductVariants.AsNoTracking()
                          join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                          join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                          where ids.Contains(v.Id)
                          select new { Variant = v, p.Status, p.DeletedAtUtc, p.TitleEn, c.VatRate }).ToListAsync(ct);

        var stock = await db.InventoryItems.AsNoTracking()
            .Where(i => ids.Contains(i.VariantId))
            .Join(db.Warehouses.Where(w => w.IsActive), i => i.WarehouseId, w => w.Id, (i, _) => i)
            .GroupBy(i => i.VariantId)
            .Select(g => new { VariantId = g.Key, Available = g.Sum(i => i.OnHand - i.Reserved) })
            .ToDictionaryAsync(x => x.VariantId, x => x.Available, ct);

        var now = clock.GetUtcNow().UtcDateTime;
        return rows.ToDictionary(r => r.Variant.Id, r =>
        {
            bool sellable = r.Status == ProductStatus.Active && r.DeletedAtUtc == null && r.Variant.Status == VariantStatus.Active;
            long unit = PricingEngine.Quote(PricingInput.From(r.Variant, r.VatRate), now, TaxMode.Inclusive).Display.ToMinorUnits();
            int available = Math.Max(0, stock.GetValueOrDefault(r.Variant.Id));
            return new CartVariantView(VariantFacts.From(r.Variant, sellable, unit, available), r.Variant.Sku, r.TitleEn);
        });
    }
}
