using Microsoft.Extensions.Configuration;
using Mechware.Application.Shipping;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Shipping;

namespace Mechware.Infrastructure.Shipping;

/// <summary>How checkout behaves before any shipping zone exists (E8.2).</summary>
public sealed class ShippingOptions
{
    public const string SectionName = "Shipping";

    /// <summary>
    /// With no active zone at all, ship for 0 instead of refusing every order. Only for Development and Testing (demo data and
    /// the older order tests have no zones); outside them the host refuses to start with it on, so production fails closed.
    /// As soon as one zone exists this has no effect: every destination is priced from the zones or refused.
    /// </summary>
    public bool AllowFreeWhenNoZones { get; init; }

    /// <summary>Reads <c>Shipping:AllowFreeWhenNoZones</c>; unset means on in Development/Testing and off everywhere else.</summary>
    public static ShippingOptions From(IConfiguration configuration, bool developmentOrTesting)
    {
        var raw = configuration[$"{SectionName}:AllowFreeWhenNoZones"];
        bool allow = developmentOrTesting;
        if (!string.IsNullOrWhiteSpace(raw) && !bool.TryParse(raw, out allow))
            throw new InvalidOperationException("Shipping:AllowFreeWhenNoZones must be true or false.");
        if (allow && !developmentOrTesting)
            throw new InvalidOperationException(
                "Shipping:AllowFreeWhenNoZones is only allowed in Development or Testing: production orders must be priced from shipping zones.");
        return new ShippingOptions { AllowFreeWhenNoZones = allow };
    }
}

/// <summary>
/// Prices an order's shipping from the active zones (<see cref="ShippingCalculator"/>), loading them once per call. Refuses with
/// a stable code rather than guessing: unknown destination, quote-only goods, or a variant with no weight.
/// </summary>
public sealed class OrderShippingQuoter(IShippingRateProvider rates, ShippingOptions options) : IOrderShippingQuoter
{
    public async Task<OrderShipping> QuoteAsync(Guid? destinationDistrictId, IReadOnlyList<ShippingQuoteLine> lines, long goodsTotalMinor, CancellationToken ct)
    {
        var zones = await rates.GetActiveZonesAsync(ct);
        if (zones.Count == 0 && options.AllowFreeWhenNoZones)
            return new OrderShipping(lines.Select(l => l.SellerId).Distinct().Select(s => new SellerShippingFee(s, 0, 0)).ToList(), 0, false);

        if (destinationDistrictId is not { } district || !zones.Any(z => z.DistrictIds.Contains(district)))
            throw new UnprocessableException(IOrderShippingQuoter.NotAvailable,
                "We cannot price delivery to this address automatically. Choose another delivery address or ask us for a quote.");

        var noWeight = lines.Where(l => l.WeightKg <= 0).Select(l => l.Sku).Distinct().ToList();
        if (noWeight.Count > 0)
            throw new UnprocessableException(IOrderShippingQuoter.WeightMissing,
                $"These items have no shipping weight yet, so delivery cannot be priced: {string.Join(", ", noWeight)}.",
                noWeight.Select(sku => new ValidationError("sku", sku)).ToList());

        var shippingLines = lines.Select(l => new ShippingLine(l.VariantId, l.SellerId, l.Quantity, ShippingLine.GramsFromKg(l.WeightKg),
            Mm(l.LengthCm), Mm(l.WidthCm), Mm(l.HeightCm))).ToList();

        switch (ShippingCalculator.Calculate(zones, district, shippingLines, goodsTotalMinor))
        {
            case ShippingQuote.Priced priced:
                return new OrderShipping(priced.PerSeller, priced.TotalMinor, priced.FreeShippingApplied);
            case ShippingQuote.QuoteOnly quoteOnly:
                var skuOf = lines.GroupBy(l => l.VariantId).ToDictionary(g => g.Key, g => g.First().Sku);
                throw new UnprocessableException(IOrderShippingQuoter.QuoteOnly,
                    "Some items are too heavy or too large for automatic delivery pricing. Ask us for a quote for this order.",
                    quoteOnly.Reasons.Select(r => new ValidationError(r.Code,
                        r.VariantId is { } v && skuOf.TryGetValue(v, out var sku) ? sku : $"seller {r.SellerId}")).ToList());
            default:
                throw new UnprocessableException(IOrderShippingQuoter.NotAvailable,
                    "We cannot price delivery to this address automatically. Choose another delivery address or ask us for a quote.");
        }
    }

    /// <summary>Unknown or non-positive sides are "unknown", never oversize (as in the calculator).</summary>
    private static int? Mm(decimal? cm) => cm is > 0 ? ShippingLine.MmFromCm(cm.Value) : null;
}
