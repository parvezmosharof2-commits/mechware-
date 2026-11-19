using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.Domain.Quotes;

/// <summary>
/// The staff-only check figures of one line (ADR-0011 §4), never in the content hash. Cost and floor are shown only with
/// <c>pricing.view_cost</c>; <see cref="FloorMinor"/> is null when no floor applied (cost unknown or no global row).
/// </summary>
public sealed record QuoteLineCheck(Guid LineId, long UnitPriceMinor, long ReferenceUnitPriceMinor, PriceSource ReferenceSource,
    long? CostMinor, long? FloorMinor, bool BelowFloor, bool BelowCost, bool OverAuthority);

/// <summary>
/// One run of <see cref="QuoteApprovalPolicy.Evaluate"/>, bound to the version and to the lines it was computed for. Only the
/// policy creates one, so a send or an approval never takes hand-made or stale flags.
/// </summary>
public sealed class QuoteApprovalCheck
{
    internal QuoteApprovalCheck(Guid versionId, string key, IReadOnlyList<QuoteLineCheck> lines, QuoteFlags flags, IReadOnlyList<Guid> belowCostLineIds)
    {
        (VersionId, Key, Lines, Flags, BelowCostLineIds) = (versionId, key, lines, flags, belowCostLineIds);
    }

    public Guid VersionId { get; }
    internal string Key { get; }

    /// <summary>In line order.</summary>
    public IReadOnlyList<QuoteLineCheck> Lines { get; }

    public QuoteFlags Flags { get; }

    /// <summary>Lines below cost while <c>Pricing:AllowBelowCost</c> is false: the send or approval is refused (409 <c>QUOTE_BELOW_COST</c>).</summary>
    public IReadOnlyList<Guid> BelowCostLineIds { get; }
}

/// <summary>
/// ADR-0011 §5, pure. Runs at send and again inside the approval transaction, with the cost, floors and resolver prices of that
/// moment. F1 below floor: <see cref="MarginFloorPolicy.EvaluateEntry"/> on the net unit price, fail closed (no cost, or no
/// global floor row, is below floor). F2 below cost: refused unless allowed. F3 over authority: see <see cref="IsOverAuthority"/>,
/// against the buyer's own resolver price now, never a previous version. F4 long validity: more days than
/// <see cref="QuoteOptions.ValidityWithoutApprovalDays"/>.
/// </summary>
public static class QuoteApprovalPolicy
{
    /// <param name="costs">Per variant (<c>ProductVariant.CostPriceMinor</c>, brand, category path); a variant without facts counts as cost unknown.</param>
    /// <param name="references">
    /// What <c>IPriceResolver</c> gives the quote's company for each line's variant and quantity at this instant, Exclusive mode
    /// (net): exactly one per line.
    /// </param>
    /// <param name="allowBelowCost"><c>Pricing:AllowBelowCost</c> (Development/Testing only).</param>
    public static QuoteApprovalCheck Evaluate(QuoteVersion version, IReadOnlyCollection<VariantCostFacts> costs, IReadOnlyCollection<MarginFloorRow> floors,
        IReadOnlyCollection<PriceResolution> references, QuoteOptions options, bool allowBelowCost)
    {
        ArgumentNullException.ThrowIfNull(version);
        ArgumentNullException.ThrowIfNull(costs);
        ArgumentNullException.ThrowIfNull(floors);
        ArgumentNullException.ThrowIfNull(references);
        ArgumentNullException.ThrowIfNull(options);
        foreach (var row in floors) MarginFloorPolicy.Validate(row);
        var cost = new Dictionary<Guid, VariantCostFacts>();
        foreach (var c in costs)
            if (!cost.TryAdd(c.VariantId, c)) throw new ValidationException("costs", "Each variant may appear once.");
        var reference = new Dictionary<Guid, PriceResolution>();
        foreach (var r in references)
        {
            if (r.Basis != PriceBasis.NetOfVat || r.UnitPriceMinor is < 1 or > PriceListLimits.MaxUnitPriceMinor)
                throw new ValidationException("references", "A reference is the buyer's net (Exclusive) resolver price.");
            if (!reference.TryAdd(r.VariantId, r)) throw new ValidationException("references", "Each variant may appear once.");
        }

        var lines = version.Lines.OrderBy(l => l.LineNo).ToList();
        if (reference.Count != lines.Count || lines.Any(l => !reference.ContainsKey(l.VariantId)))
            throw new ConflictException("QUOTE_CHANGED", "The buyer's prices were looked up for other lines. Check them again.");

        var checks = lines.Select(l =>
        {
            var floor = MarginFloorPolicy.EvaluateEntry(l.Id, l.UnitPriceMinor, cost.GetValueOrDefault(l.VariantId), floors);
            var r = reference[l.VariantId];
            return new QuoteLineCheck(l.Id, l.UnitPriceMinor, r.UnitPriceMinor, r.Source, floor.CostMinor, floor.FloorMinor, floor.BelowFloor,
                floor.BelowCost, IsOverAuthority(l.UnitPriceMinor, r.UnitPriceMinor, options.DiscountAuthorityBasisPoints));
        }).ToList();
        var flags = new QuoteFlags(checks.Where(c => c.BelowFloor).Select(c => c.LineId).ToList(),
            checks.Where(c => c.OverAuthority).Select(c => c.LineId).ToList(), version.ValidityDays > options.ValidityWithoutApprovalDays);
        IReadOnlyList<Guid> belowCost = allowBelowCost ? [] : checks.Where(c => c.BelowCost).Select(c => c.LineId).ToList();
        return new QuoteApprovalCheck(version.Id, version.CheckKey(), checks, flags, belowCost);
    }

    /// <summary>F3 in integers: <c>unit × 10,000 &lt; reference × (10,000 − basisPoints)</c>. Exactly at the limit is within authority.</summary>
    public static bool IsOverAuthority(long unitPriceMinor, long referenceUnitPriceMinor, int authorityBasisPoints) =>
        checked(unitPriceMinor * 10_000) < checked(referenceUnitPriceMinor * (10_000 - authorityBasisPoints));
}
