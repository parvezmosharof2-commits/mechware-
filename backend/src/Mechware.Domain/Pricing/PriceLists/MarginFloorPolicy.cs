using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Domain.Pricing.PriceLists;

/// <summary>How specific a margin floor row is (ADR-0008 §1). Values are persisted, never renumber.</summary>
public enum MarginFloorScope { Global = 1, Category = 2, Brand = 3, BrandAndCategory = 4 }

/// <summary>A margin floor row (data, table <c>margin_floors</c>): a minimum markup on cost in percent, at most two decimals.</summary>
public sealed record MarginFloorRow(MarginFloorScope Scope, Guid? BrandId, Guid? CategoryId, decimal MinMarkupPercent);

/// <summary>What the floor check needs to know about a variant at one instant.</summary>
/// <param name="CostMinor">VAT-exclusive cost (<c>ProductVariant.CostPriceMinor</c>); null or ≤ 0 counts as unknown (fail closed).</param>
/// <param name="CategoryPath">The product's own category first, then its ancestors, nearest first.</param>
/// <param name="VatRate">Used only to bring a <see cref="PriceListScope.General"/> entry to net (D2.2 rule).</param>
public sealed record VariantCostFacts(Guid VariantId, long? CostMinor, Guid? BrandId, IReadOnlyList<Guid> CategoryPath, decimal VatRate);

/// <summary>
/// The floor check of one entry. Cost and floor are confidential: only these values carry them, and the API shows them
/// only with <c>pricing.view_cost</c>. <see cref="FloorSource"/> is null when no floor applied (cost unknown or no global row).
/// </summary>
public sealed record EntryFloorEvaluation(
    Guid EntryId, long NetUnitPriceMinor, long? FloorMinor, long? CostMinor, bool BelowFloor, bool BelowCost, bool CostUnknown,
    MarginFloorScope? FloorSource);

/// <summary>
/// The floor check of a whole price list, bound to the content it was computed for (<see cref="ContentHash"/>). Only
/// <see cref="MarginFloorPolicy.Evaluate"/> creates one, so the aggregate never accepts a hand-made or stale evaluation.
/// </summary>
public sealed class MarginFloorEvaluation
{
    internal MarginFloorEvaluation(Guid priceListId, string contentHash, IReadOnlyList<EntryFloorEvaluation> entries)
    {
        PriceListId = priceListId;
        ContentHash = contentHash;
        Entries = entries;
    }

    public Guid PriceListId { get; }
    public string ContentHash { get; }
    public IReadOnlyList<EntryFloorEvaluation> Entries { get; }

    /// <summary>The ids the approver must acknowledge, sorted.</summary>
    public IReadOnlyList<Guid> BelowFloorEntryIds => Entries.Where(e => e.BelowFloor).Select(e => e.EntryId).Order().ToList();
}

/// <summary>
/// Margin floor (ADR-0008 §4, owner decision 3): <c>floorMinor = ceil(costMinor × (100 + minMarkupPercent) / 100)</c> in
/// integers, rounded up. The most specific row wins: brand + category (own, then nearest ancestor) → brand → category (own,
/// then nearest ancestor) → global; several rows on one level → the highest markup. Fails closed: no cost, or no global
/// row at all, makes the entry below floor. The entry is compared on its net amount.
/// </summary>
public static class MarginFloorPolicy
{
    /// <summary>Owner decision 3: 10 % on cost, global. D2.4 seeds it as the single global row.</summary>
    public const decimal DefaultGlobalMinMarkupPercent = 10m;

    public const decimal MaxMinMarkupPercent = 1_000m;

    /// <summary>Rounded up, so rounding never lets a price under the floor.</summary>
    public static long FloorMinor(long costMinor, decimal minMarkupPercent)
    {
        if (costMinor is < 0 or > PriceListLimits.MaxUnitPriceMinor) throw new ValidationException("cost", "Cost is out of range.");
        var numerator = checked(costMinor * (10_000 + Hundredths(minMarkupPercent)));
        return (numerator + 9_999) / 10_000;
    }

    public static MarginFloorEvaluation Evaluate(PriceList list, IReadOnlyCollection<VariantCostFacts> variants, IReadOnlyCollection<MarginFloorRow> floors)
    {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(variants);
        ArgumentNullException.ThrowIfNull(floors);
        foreach (var row in floors) Validate(row);
        var facts = new Dictionary<Guid, VariantCostFacts>();
        foreach (var v in variants)
            if (!facts.TryAdd(v.VariantId, v)) throw new ValidationException("variants", "Each variant may appear once.");

        var result = list.Entries
            .Select(e =>
            {
                var f = facts.GetValueOrDefault(e.VariantId);
                var net = list.Scope == PriceListScope.General && f is not null ? ToNet(e.UnitPriceMinor, f.VatRate) : e.UnitPriceMinor;
                return EvaluateEntry(e.Id, net, f, floors);
            })
            .ToList();
        return new MarginFloorEvaluation(list.Id, list.ComputeContentHash(), result);
    }

    /// <summary>One net price against the variant's floor; usable later for sale prices and promotions (§6). Missing facts = cost unknown.</summary>
    public static EntryFloorEvaluation EvaluateEntry(Guid entryId, long netUnitPriceMinor, VariantCostFacts? facts, IReadOnlyCollection<MarginFloorRow> floors)
    {
        if (facts?.CostMinor is not { } cost || cost is <= 0 or > PriceListLimits.MaxUnitPriceMinor)
            return new(entryId, netUnitPriceMinor, null, null, BelowFloor: true, BelowCost: false, CostUnknown: true, null);
        var belowCost = netUnitPriceMinor < cost;
        if (FindFloor(facts, floors) is not { } row)
            return new(entryId, netUnitPriceMinor, null, cost, BelowFloor: true, belowCost, CostUnknown: false, null);
        var floor = FloorMinor(cost, row.MinMarkupPercent);
        return new(entryId, netUnitPriceMinor, floor, cost, netUnitPriceMinor < floor, belowCost, CostUnknown: false, row.Scope);
    }

    /// <summary>The applicable row, or null when there is no global row (fail closed even if a more specific row matches).</summary>
    public static MarginFloorRow? FindFloor(VariantCostFacts facts, IReadOnlyCollection<MarginFloorRow> floors)
    {
        ArgumentNullException.ThrowIfNull(facts);
        foreach (var row in floors) Validate(row);
        if (!floors.Any(r => r.Scope == MarginFloorScope.Global)) return null;
        var path = facts.CategoryPath ?? [];
        if (facts.BrandId is { } brand)
        {
            foreach (var category in path)
                if (Highest(floors, r => r.Scope == MarginFloorScope.BrandAndCategory && r.BrandId == brand && r.CategoryId == category) is { } bc)
                    return bc;
            if (Highest(floors, r => r.Scope == MarginFloorScope.Brand && r.BrandId == brand) is { } b) return b;
        }

        foreach (var category in path)
            if (Highest(floors, r => r.Scope == MarginFloorScope.Category && r.CategoryId == category) is { } c) return c;
        return Highest(floors, r => r.Scope == MarginFloorScope.Global);
    }

    /// <summary>
    /// A General entry (VAT-inclusive) on the net basis, rounded down like the resolver (<see cref="PriceResolver.ToOrderBasis"/>,
    /// D2.2) but without its 1-poisha minimum: a 1-poisha gross price is 0.95 net, and the floor check must see that (fail closed).
    /// </summary>
    private static long ToNet(long grossMinor, decimal vatRate)
    {
        if (vatRate is < 0m or > 0.5m) throw new ValidationException("vatRate", "VAT rate must be between 0 and 0.5.");
        var net = PriceResolver.ToOrderBasis(grossMinor, vatRate, TaxMode.Exclusive);
        return net == 1 && grossMinor < 1m + vatRate ? 0 : net;
    }

    private static MarginFloorRow? Highest(IEnumerable<MarginFloorRow> floors, Func<MarginFloorRow, bool> match) =>
        floors.Where(match).MaxBy(r => r.MinMarkupPercent);

    private static long Hundredths(decimal percent)
    {
        if (percent is < 0m or > MaxMinMarkupPercent || decimal.Round(percent, 2) != percent)
            throw new ValidationException("minMarkupPercent", $"A markup is 0 to {MaxMinMarkupPercent} % with at most two decimals.");
        return (long)(percent * 100m);
    }

    private static void Validate(MarginFloorRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var shapeOk = row.Scope switch
        {
            MarginFloorScope.Global => row.BrandId is null && row.CategoryId is null,
            MarginFloorScope.Category => row.BrandId is null && row.CategoryId is not null,
            MarginFloorScope.Brand => row.BrandId is not null && row.CategoryId is null,
            MarginFloorScope.BrandAndCategory => row.BrandId is not null && row.CategoryId is not null,
            _ => false,
        };
        if (!shapeOk) throw new ValidationException("scope", "A margin floor row's brand and category must match its scope.");
        Hundredths(row.MinMarkupPercent);
    }
}
