using Microsoft.EntityFrameworkCore;
using Mechware.Application.Pricing;
using Mechware.Domain.Catalog;
using Mechware.Domain.Companies;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Pricing;

/// <summary>
/// <see cref="ICompanyPriceService"/>: the buyer's company prices through the order's own path (ADR-0008 §5, §11). The same
/// catalogue read and availability rule as <c>OrderService.PriceAsync</c>, one <see cref="IPriceResolver"/> call in net mode
/// for the verified company at one instant, and <see cref="OrderPricing.PriceLine"/> for the line, so what is shown is what an
/// order for that quantity is charged before promotions and shipping. Nothing here is cached and nothing is written.
/// </summary>
public sealed class CompanyPriceService(MechwareDbContext db, IPriceResolver prices, TimeProvider clock) : ICompanyPriceService
{
    public async Task<CompanyPricesDto> GetAsync(Guid actorUserId, Guid companyId, string? items, CancellationToken ct = default)
    {
        var verifiedCompanyId = await VerifiedCompanyAsync(actorUserId, companyId, ct);
        var requested = CompanyPriceQuery.Parse(items);
        var now = clock.GetUtcNow().UtcDateTime;
        var ids = requested.Select(r => r.VariantId).ToList();

        // Same availability as an order: product and variant active (OrderService.PriceAsync). Nothing else is told apart, so an
        // unpublished variant looks exactly like one that does not exist.
        var catalogue = (await (from v in db.ProductVariants.AsNoTracking()
                                join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                                join c in db.Categories.AsNoTracking() on p.CategoryId equals c.Id
                                join s in db.Sellers.AsNoTracking() on p.SellerId equals s.Id
                                where ids.Contains(v.Id) && p.Status == ProductStatus.Active && v.Status == VariantStatus.Active
                                select new { Variant = v, c.VatRate }).ToListAsync(ct))
            .ToDictionary(r => r.Variant.Id);

        // The tiers of this company's own approved contracts for these variants: the rows a break table can show. Which of them
        // applies is the resolver's decision, below.
        var tiers = await db.PriceLists.AsNoTracking()
            .Where(p => p.Status == PriceListStatus.Approved && p.Scope == PriceListScope.Company && p.CompanyId == verifiedCompanyId && p.ValidToUtc > now)
            .SelectMany(p => p.Entries.Where(e => ids.Contains(e.VariantId)).Select(e => new { ListId = p.Id, p.ValidToUtc, EntryId = e.Id, e.VariantId, e.MinQty }))
            .ToListAsync(ct);

        var asked = requested.Where(r => catalogue.ContainsKey(r.VariantId)).ToList();
        var tierQuantities = tiers.Where(t => catalogue.ContainsKey(t.VariantId)).Select(t => new CompanyPriceItem(t.VariantId, t.MinQty)).Distinct().ToList();
        var lines = asked.Concat(tierQuantities).Distinct()
            .Select(r => new PriceRequestLine(r.VariantId, r.Quantity, PricingInput.From(catalogue[r.VariantId].Variant, catalogue[r.VariantId].VatRate)))
            .ToList();
        // One resolver call, one query, one instant for the asked quantities and every tier.
        IReadOnlyList<ResolvedPrice> resolved = lines.Count == 0 ? [] : await prices.ResolveAsync(verifiedCompanyId, lines, TaxMode.Exclusive, now, ct);
        var byItem = lines.Zip(resolved).ToDictionary(x => new CompanyPriceItem(x.First.VariantId, x.First.Quantity), x => x.Second);

        return new CompanyPricesDto(verifiedCompanyId, now, requested.Select(r =>
        {
            if (!catalogue.TryGetValue(r.VariantId, out var row)) return new CompanyPriceDto { VariantId = r.VariantId };
            var price = byItem[r];
            var line = OrderPricing.PriceLine(new OrderLineDraft(Guid.Empty, r.VariantId, Guid.Empty, row.Variant.Sku, string.Empty,
                r.Quantity, price.Price.UnitPriceMinor, row.VatRate, 0, price.Price), TaxMode.Exclusive);
            var contract = price.Price.Source == PriceSource.Contract;

            // A tier is shown when, at its own quantity, the resolver picks that very contract entry (not a cheaper public price,
            // not a list outside its window or superseded).
            var breaks = tiers.Where(t => t.VariantId == r.VariantId)
                .Where(t => byItem[new CompanyPriceItem(t.VariantId, t.MinQty)].Price is { Source: PriceSource.Contract } p && p.EntryId == t.EntryId)
                .Select(t => new CompanyPriceBreakDto(t.MinQty, byItem[new CompanyPriceItem(t.VariantId, t.MinQty)].Price.UnitPriceMinor))
                .DistinctBy(b => b.MinQty).OrderBy(b => b.MinQty).ToList();

            return new CompanyPriceDto
            {
                VariantId = r.VariantId,
                Status = CompanyPriceDto.Available,
                Quantity = r.Quantity,
                UnitPriceMinorNet = price.Price.UnitPriceMinor,
                PublicUnitPriceMinorNet = price.Price.PublicUnitPriceMinor,
                GuestUnitPriceMinorInclVat = price.GuestUnitPriceMinor,
                Source = Wire(price.Price.Source),
                PriceListId = contract ? price.Price.PriceListId : null,
                ContractRef = contract ? price.Price.ContractRef : null,
                ValidToUtc = contract ? tiers.FirstOrDefault(t => t.ListId == price.Price.PriceListId)?.ValidToUtc : null,
                TaxMode = "exclusive",
                VatRate = row.VatRate,
                LineNetMinor = line.NetMinor,
                LineTaxMinor = line.TaxMinor,
                LineTotalMinor = line.TotalMinor,
                Breaks = breaks.Any(b => b.MinQty > 1) ? breaks : [],
                QuantityProblem = row.Variant.QuantityProblem(r.Quantity),
            };
        }).ToList());
    }

    /// <summary>
    /// Who may see a company's prices: who may order for it (OrderService.RequireCompanyMayBuyAsync), i.e. an active, accepted
    /// member holding the Buyer role of an active company. Everyone else, including members without the Buyer role and members
    /// of a company not (or no longer) active, gets the one "Company not found." answer, so nothing tells the cases apart.
    /// </summary>
    private async Task<Guid> VerifiedCompanyAsync(Guid actorUserId, Guid companyId, CancellationToken ct)
    {
        var member = await db.CompanyMembers.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.UserId == actorUserId && m.IsActive && m.AcceptedAtUtc != null)
            .FirstOrDefaultAsync(ct);
        if (member is null || !member.Has(CompanyRoles.Buyer)
            || !await db.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId && c.Status == CompanyStatus.Active, ct))
            throw new KeyNotFoundException("Company not found.");
        return member.CompanyId;
    }

    /// <summary>The snake_case name the order line's <c>priceSource</c> uses.</summary>
    private static string Wire(PriceSource source) =>
        string.Concat(source.ToString().Select((c, i) => i > 0 && char.IsUpper(c) ? "_" + char.ToLowerInvariant(c) : char.ToLowerInvariant(c).ToString()));
}
