using Mechware.Domain.Shipping;

namespace Mechware.Application.Shipping;

/// <summary>
/// One order line as the database describes it, for shipping: weight from the variant, sides from the product. Built by the
/// server from catalogue rows only; nothing here ever comes from the client.
/// </summary>
public sealed record ShippingQuoteLine(
    Guid VariantId, string Sku, Guid SellerId, int Quantity, decimal WeightKg, decimal? LengthCm, decimal? WidthCm, decimal? HeightCm);

/// <summary>Shipping for a whole order: one fee per seller (what <c>SellerTerms</c> takes) and their exact sum.</summary>
public sealed record OrderShipping(IReadOnlyList<SellerShippingFee> PerSeller, long TotalMinor, bool FreeShippingApplied);

/// <summary>
/// The pricing seam between orders and shipping zones (E8.2). The order service resolves the destination and builds the lines;
/// the quoter decides the fee or refuses. Refusals are <c>UnprocessableException</c>s (HTTP 422) with one of the codes below.
/// </summary>
public interface IOrderShippingQuoter
{
    /// <summary>No active zone serves the destination (or the address's district is not a known district).</summary>
    public const string NotAvailable = "SHIPPING_NOT_AVAILABLE";

    /// <summary>Heavy, oversize or above the last weight band: the buyer must ask for a quote (RFQ, D4).</summary>
    public const string QuoteOnly = "SHIPPING_QUOTE_ONLY";

    /// <summary>A variant has no usable weight; the seller must fix the listing. Never priced as free.</summary>
    public const string WeightMissing = "VARIANT_WEIGHT_MISSING";

    /// <param name="destinationDistrictId">The <c>bd_locations</c> district the order ships to; null when the address names no known district.</param>
    /// <param name="goodsTotalMinor">
    /// The free-shipping basis: the goods the buyer pays for, after discounts, VAT included, shipping excluded (the sum of the
    /// line totals). The same number in VAT-inclusive and VAT-exclusive orders, and what the buyer sees in the cart.
    /// </param>
    Task<OrderShipping> QuoteAsync(Guid? destinationDistrictId, IReadOnlyList<ShippingQuoteLine> lines, long goodsTotalMinor, CancellationToken ct);
}
