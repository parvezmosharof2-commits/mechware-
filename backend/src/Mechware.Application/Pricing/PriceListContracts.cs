using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.Application.Pricing;

/// <summary>One variant of an order, with the catalogue facts the caller read in the same pricing pass (ADR-0008 §5).</summary>
/// <param name="Quantity">All units of the variant in the order (lines of one variant are added up first), 1 … 10,000.</param>
/// <param name="Catalogue">The variant's catalogue price, sale and VAT rate; prices are VAT-inclusive for everyone (owner decision 2).</param>
public sealed record PriceRequestLine(Guid VariantId, int Quantity, PricingInput Catalogue);

/// <param name="Price">
/// The resolver's answer on the order's basis: unit price, source, list id, version, entry id, contract number (a contract
/// price only) and the public price. This is what the order line snapshots (§8).
/// </param>
/// <param name="GuestUnitPriceMinor">What a guest pays per unit for this quantity at the same instant (VAT-inclusive).</param>
public sealed record ResolvedPrice(PriceResolution Price, long GuestUnitPriceMinor);

/// <summary>
/// The one place a buyer's unit price is decided (ADR-0008 §5): the price lists that apply to the buyer at one instant,
/// handed to the pure <see cref="PriceResolver"/>. Orders, the buyer price endpoint (D2.10) and quotes (D4) call it.
/// </summary>
public interface IPriceResolver
{
    /// <param name="verifiedCompanyId">
    /// The company of a company order, taken from the membership check of the same request (B1), never from the request
    /// body. Null for guest and personal orders, which then never get a negotiated price.
    /// </param>
    /// <param name="mode">Exclusive (net) for a company order, Inclusive for every other order.</param>
    /// <param name="asOfUtc">The order's single server instant.</param>
    /// <returns>One result per line, in the order of <paramref name="lines"/>.</returns>
    Task<IReadOnlyList<ResolvedPrice>> ResolveAsync(
        Guid? verifiedCompanyId, IReadOnlyList<PriceRequestLine> lines, TaxMode mode, DateTime asOfUtc, CancellationToken ct = default);
}

/// <summary>The cost facts and margin-floor rows of one instant (ADR-0008 §4), and whether this host allows prices below cost.</summary>
/// <param name="Costs">One per variant found; a variant left out counts as cost unknown (below the floor, fail closed).</param>
public sealed record PriceCheckFacts(IReadOnlyList<VariantCostFacts> Costs, IReadOnlyList<MarginFloorRow> Floors, bool AllowBelowCost);

/// <summary>
/// What a price that is not a price-list entry is checked against (quotes, ADR-0011 §5): the same cost, brand, category ancestry and
/// floor rows the price-list approval reads, and <c>Pricing:AllowBelowCost</c>. Read fresh on every call.
/// </summary>
public interface IPriceCheckFacts
{
    Task<PriceCheckFacts> ForVariantsAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct = default);
}
