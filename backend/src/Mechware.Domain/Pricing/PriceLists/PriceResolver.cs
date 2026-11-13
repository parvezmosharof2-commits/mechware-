using Mechware.Domain.Common;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Domain.Pricing.PriceLists;

/// <summary>
/// Pure price resolution (ADR-0008 §5, rule A, owner decision 1). The buyer pays the <b>lowest valid</b> candidate;
/// the order Contract &gt; GroupList &gt; QuantityBreak &gt; Sale &gt; List only breaks ties and names the source, then the
/// later approval, then the smaller list id, then the smaller entry id, so the result never depends on input order.
/// <para>
/// <b>One basis.</b> Every candidate is brought to the order's basis before comparing: gross for
/// <see cref="TaxMode.Inclusive"/>, net for <see cref="TaxMode.Exclusive"/>. Negotiated entries are net (§2) and are only
/// considered for a company order, which must be Exclusive, so they are never converted. Public prices (list, sale,
/// General breaks) are gross (owner decision 2); on the net basis they become
/// <c>max(1, floor(gross / (1 + VAT)))</c>. Rounding <b>down</b> means: a negotiated price wins only when it is not above
/// the exact public net price, re-adding VAT never exceeds the gross price, and when the public price wins the buyer
/// pays at most what the exact conversion gives.
/// </para>
/// </summary>
public static class PriceResolver
{
    /// <summary>
    /// The instant a version starts applying: <c>max(validFrom, approvedAt + ActivationDelay)</c> (§10), so a backdated
    /// <paramref name="validFromUtc"/> never applies before the approval. Null when the version was never approved.
    /// </summary>
    public static DateTime? EffectiveFromUtc(DateTime validFromUtc, DateTime? approvedAtUtc)
    {
        if (approvedAtUtc is not { } approved || approved > DateTime.MaxValue - PriceListLimits.ActivationDelay) return null;
        var earliest = approved + PriceListLimits.ActivationDelay;
        return validFromUtc > earliest ? validFromUtc : earliest;
    }

    /// <summary>A public (VAT-inclusive) price on the order's basis; net is rounded down to whole poisha, never below 1.</summary>
    public static long ToOrderBasis(long grossMinor, decimal vatRate, TaxMode mode)
    {
        if (mode == TaxMode.Inclusive) return grossMinor;
        var net = (long)decimal.Floor(grossMinor / (1m + vatRate));
        return net < 1 ? 1 : net;
    }

    public static PriceResolution Resolve(PriceResolutionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var catalogue = request.Catalogue ?? throw new ValidationException("catalogue", "Catalogue price is required.");
        Validate(request, catalogue);

        var mode = request.Mode;
        var currency = catalogue.Price.Currency;
        var publicCandidates = new List<Candidate>
        {
            new(ToOrderBasis(catalogue.Price.ToMinorUnits(), catalogue.VatRate, mode), PriceSource.List, null, null, null, null, null),
        };
        if (PricingEngine.IsSaleActive(catalogue, request.AsOfUtc))
        {
            var sale = ToOrderBasis(catalogue.SalePrice!.Value.ToMinorUnits(), catalogue.VatRate, mode);
            publicCandidates.Add(new(sale, PriceSource.Sale, null, null, null, null, null));
        }

        var applicable = ApplicableLists(request.Lists ?? [], request.AsOfUtc, currency);
        foreach (var list in applicable)
        {
            if (list.Scope != PriceListScope.General || list.CompanyId is not null) continue;
            if (BestEntry(list, request.VariantId, request.Quantity) is not { } entry) continue;
            publicCandidates.Add(new(ToOrderBasis(entry.UnitPriceMinor, catalogue.VatRate, mode), PriceSource.QuantityBreak,
                list.Id, list.Version, entry.Id, null, list.ApprovedAtUtc));
        }

        var candidates = new List<Candidate>(publicCandidates);
        if (request.VerifiedCompanyId is { } companyId)
        {
            foreach (var list in applicable)
            {
                if (NegotiatedSource(list, companyId, request.GroupSeriesId) is not { } source) continue;
                if (BestEntry(list, request.VariantId, request.Quantity) is not { } entry) continue;
                candidates.Add(new(entry.UnitPriceMinor, source, list.Id, list.Version, entry.Id,
                    source == PriceSource.Contract ? list.ContractRef : null, list.ApprovedAtUtc));
            }
        }

        var best = Choose(candidates);
        var basis = mode == TaxMode.Inclusive ? PriceBasis.GrossInclVat : PriceBasis.NetOfVat;
        return new PriceResolution(request.VariantId, best.UnitPriceMinor, best.Source, basis, best.ListId, best.Version,
            best.EntryId, best.ContractRef, Choose(publicCandidates).UnitPriceMinor);
    }

    private static void Validate(PriceResolutionRequest request, PricingInput catalogue)
    {
        if (request.Quantity is < 1 or > OrderLimits.MaxQuantityPerLine)
            throw new ValidationException("quantity", $"Quantity must be between 1 and {OrderLimits.MaxQuantityPerLine}.");
        if (catalogue.VatRate is < 0m or > 0.5m) throw new ValidationException("vatRate", "VAT rate must be between 0 and 0.5.");
        if (catalogue.Price.ToMinorUnits() is < 1 or > PriceListLimits.MaxUnitPriceMinor)
            throw new ValidationException("price", "The catalogue price must be positive and not above the maximum unit price.");
        if (request.Mode is not (TaxMode.Inclusive or TaxMode.Exclusive)) throw new ValidationException("mode", "Unknown tax mode.");
        // Negotiated prices are net (§2); comparing them with gross prices would mix two bases.
        if (request.VerifiedCompanyId is not null && request.Mode != TaxMode.Exclusive)
            throw new ValidationException("mode", "A company order is priced net of VAT.");
    }

    /// <summary>
    /// Approved, inside <c>[start, validTo)</c>, in the order's currency, and no higher version of its series has started.
    /// "Started" includes a version withdrawn at or after its start and one that has expired since, so neither a
    /// withdrawal nor an expiry ever brings an older version back (§3).
    /// </summary>
    private static List<PriceListInput> ApplicableLists(IEnumerable<PriceListInput> lists, DateTime asOf, Currency currency)
    {
        var all = lists.Where(l => l is not null).ToList();
        var latestStarted = new Dictionary<Guid, int>();
        foreach (var list in all)
        {
            if (!HasStarted(list, asOf)) continue;
            latestStarted[list.SeriesId] = latestStarted.TryGetValue(list.SeriesId, out var v) ? Math.Max(v, list.Version) : list.Version;
        }

        return all.Where(l => l.Status == PriceListStatus.Approved
                && HasStarted(l, asOf)
                && asOf < l.ValidToUtc
                && l.Currency == currency
                && latestStarted[l.SeriesId] == l.Version)
            .ToList();
    }

    private static bool HasStarted(PriceListInput list, DateTime asOf)
    {
        if (EffectiveFromUtc(list.ValidFromUtc, list.ApprovedAtUtc) is not { } start) return false;
        if (start > asOf || start >= list.ValidToUtc) return false; // an empty window never applied to anything
        return list.Status switch
        {
            PriceListStatus.Approved => true,
            PriceListStatus.Withdrawn => list.WithdrawnAtUtc is not { } withdrawn || withdrawn >= start,
            _ => false,
        };
    }

    private static PriceSource? NegotiatedSource(PriceListInput list, Guid companyId, Guid? groupSeriesId) => list.Scope switch
    {
        PriceListScope.Company when list.CompanyId == companyId => PriceSource.Contract,
        PriceListScope.Group when list.CompanyId is null && groupSeriesId == list.SeriesId => PriceSource.GroupList,
        _ => null,
    };

    /// <summary>The valid entry of the variant with the largest minQty not above the quantity (a duplicate minQty: the cheaper, then the smaller id).</summary>
    private static PriceListEntryInput? BestEntry(PriceListInput list, Guid variantId, int quantity)
    {
        PriceListEntryInput? best = null;
        foreach (var e in list.Entries ?? [])
        {
            if (e is null || e.VariantId != variantId || e.MinQty < 1 || e.MinQty > quantity) continue;
            if (e.UnitPriceMinor is < 1 or > PriceListLimits.MaxUnitPriceMinor) continue; // never zero, negative or absurd
            if (best is null
                || e.MinQty > best.MinQty
                || (e.MinQty == best.MinQty && (e.UnitPriceMinor < best.UnitPriceMinor
                    || (e.UnitPriceMinor == best.UnitPriceMinor && e.Id.CompareTo(best.Id) < 0))))
                best = e;
        }

        return best;
    }

    private static Candidate Choose(List<Candidate> candidates)
    {
        var best = candidates[0];
        for (var i = 1; i < candidates.Count; i++)
            if (Compare(candidates[i], best) < 0) best = candidates[i];
        return best;
    }

    /// <summary>Total order: lower price, then more specific source, then later approval, then smaller list id, then smaller entry id.</summary>
    private static int Compare(Candidate a, Candidate b)
    {
        var c = a.UnitPriceMinor.CompareTo(b.UnitPriceMinor);
        if (c != 0) return c;
        c = Specificity(b.Source).CompareTo(Specificity(a.Source));
        if (c != 0) return c;
        c = Nullable.Compare(b.ApprovedAtUtc, a.ApprovedAtUtc);
        if (c != 0) return c;
        c = Nullable.Compare(a.ListId, b.ListId);
        return c != 0 ? c : Nullable.Compare(a.EntryId, b.EntryId);
    }

    private static int Specificity(PriceSource source) => source switch
    {
        PriceSource.Contract => 5,
        PriceSource.GroupList => 4,
        PriceSource.QuantityBreak => 3,
        PriceSource.Sale => 2,
        _ => 1,
    };

    private sealed record Candidate(
        long UnitPriceMinor, PriceSource Source, Guid? ListId, int? Version, Guid? EntryId, string? ContractRef, DateTime? ApprovedAtUtc);
}
