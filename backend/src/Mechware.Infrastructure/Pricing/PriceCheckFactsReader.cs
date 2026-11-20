using Microsoft.EntityFrameworkCore;
using Mechware.Application.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Pricing;

/// <summary>
/// <see cref="IPriceCheckFacts"/>: cost facts (cost price, brand, category ancestry, VAT rate) and the margin-floor rows of this
/// instant, read the same way for a price-list approval (<see cref="PriceListService"/>) and for a quote (ADR-0011 §5).
/// </summary>
public sealed class PriceCheckFactsReader(MechwareDbContext db, PricingOptions options) : IPriceCheckFacts
{
    public async Task<PriceCheckFacts> ForVariantsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct = default) =>
        new(await CostFactsAsync(db, variantIds, ct), await FloorsAsync(db, ct), options.AllowBelowCost);

    internal static async Task<List<VariantCostFacts>> CostFactsAsync(MechwareDbContext db, IReadOnlyCollection<Guid> variantIds, CancellationToken ct)
    {
        var ids = variantIds.Distinct().ToList();
        if (ids.Count == 0) return [];
        var variants = await (from v in db.ProductVariants.AsNoTracking()
                              where ids.Contains(v.Id)
                              join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                              join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                              select new { v.Id, v.CostPriceMinor, p.BrandId, p.CategoryId, c.VatRate }).ToListAsync(ct);
        var parents = await db.Categories.AsNoTracking().Select(c => new { c.Id, c.ParentId }).ToDictionaryAsync(c => c.Id, c => c.ParentId, ct);
        return variants.Select(v =>
        {
            var path = new List<Guid>();
            for (Guid? c = v.CategoryId; c is { } cur && path.Count < 32 && !path.Contains(cur); c = parents.GetValueOrDefault(cur)) path.Add(cur);
            return new VariantCostFacts(v.Id, v.CostPriceMinor, v.BrandId, path, v.VatRate);
        }).ToList();
    }

    internal static async Task<List<MarginFloorRow>> FloorsAsync(MechwareDbContext db, CancellationToken ct) =>
        (await db.MarginFloors.AsNoTracking().ToListAsync(ct)).Select(f => f.ToRow()).ToList();
}
