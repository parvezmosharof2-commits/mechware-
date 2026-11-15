using Microsoft.EntityFrameworkCore;
using Mechware.Application.Pricing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Pricing;

/// <summary>
/// <see cref="IPriceResolver"/> over the database (ADR-0008 §5). One query loads <b>every</b> Approved and Withdrawn version of
/// the public (General) series and of the ordering company's contract series, with their entries for the requested variants
/// only. Nothing is filtered by a time window in SQL: a later version that has started (even if withdrawn or expired since)
/// must be seen, or an older cheaper version would come back; the pure resolver decides with the order's instant. Group
/// lists are not used at launch (owner decision 4), so no group series is passed.
/// </summary>
public sealed class PriceListResolver(MechwareDbContext db) : IPriceResolver
{
    public async Task<IReadOnlyList<ResolvedPrice>> ResolveAsync(
        Guid? verifiedCompanyId, IReadOnlyList<PriceRequestLine> lines, TaxMode mode, DateTime asOfUtc, CancellationToken ct = default)
    {
        if (lines.Count == 0) return [];
        // Negotiated prices are net (§2): a company order is priced net, every other order gross.
        if (mode != (verifiedCompanyId is null ? TaxMode.Inclusive : TaxMode.Exclusive))
            throw new ValidationException("mode", "A company order is priced net of VAT, every other order VAT-inclusive.");

        var variantIds = lines.Select(l => l.VariantId).Distinct().ToList();
        var query = db.PriceLists.AsNoTracking()
            .Where(p => p.Status == PriceListStatus.Approved || p.Status == PriceListStatus.Withdrawn);
        query = verifiedCompanyId is { } companyId
            ? query.Where(p => p.Scope == PriceListScope.General || (p.Scope == PriceListScope.Company && p.CompanyId == companyId))
            : query.Where(p => p.Scope == PriceListScope.General);
        var lists = (await query
                .Select(p => new
                {
                    p.Id,
                    p.SeriesId,
                    p.Version,
                    p.Scope,
                    p.CompanyId,
                    p.ContractRef,
                    p.Currency,
                    p.Status,
                    p.ValidFromUtc,
                    p.ValidToUtc,
                    p.ApprovedAtUtc,
                    p.WithdrawnAtUtc,
                    Entries = p.Entries.Where(e => variantIds.Contains(e.VariantId))
                        .Select(e => new PriceListEntryInput(e.Id, e.VariantId, e.MinQty, e.UnitPriceMinor)).ToList(),
                })
                .ToListAsync(ct))
            .Select(p => new PriceListInput(p.Id, p.SeriesId, p.Version, p.Scope, p.CompanyId, p.ContractRef, p.Currency, p.Status,
                p.ValidFromUtc, p.ValidToUtc, p.ApprovedAtUtc, p.WithdrawnAtUtc, p.Entries))
            .ToList();

        return lines.Select(l =>
        {
            var price = PriceResolver.Resolve(new PriceResolutionRequest(l.VariantId, l.Quantity, l.Catalogue, mode, verifiedCompanyId, null, asOfUtc, lists));
            // The guest's price for the same quantity and instant: the free-shipping basis of a company order (OrderPricing).
            var guest = verifiedCompanyId is null
                ? price.UnitPriceMinor
                : PriceResolver.Resolve(new PriceResolutionRequest(l.VariantId, l.Quantity, l.Catalogue, TaxMode.Inclusive, null, null, asOfUtc, lists)).UnitPriceMinor;
            return new ResolvedPrice(price, guest);
        }).ToList();
    }
}
