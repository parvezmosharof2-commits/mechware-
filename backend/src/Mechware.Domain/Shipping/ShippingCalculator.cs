using Mechware.Domain.Common.Exceptions;

namespace Mechware.Domain.Shipping;

/// <summary>One purchased line for shipping: weight in whole grams per unit, optional sides in whole millimetres.</summary>
public sealed record ShippingLine(Guid VariantId, Guid SellerId, int Quantity, int UnitGrams, int? LengthMm = null, int? WidthMm = null, int? HeightMm = null)
{
    /// <summary>Catalogue weights are decimal kg; a partial gram counts as a whole one (never ships lighter than it weighs).</summary>
    public static int GramsFromKg(decimal kg) => checked((int)decimal.Ceiling(kg * 1000m));

    /// <summary>Catalogue sides are decimal cm; a partial millimetre counts as a whole one.</summary>
    public static int MmFromCm(decimal cm) => checked((int)decimal.Ceiling(cm * 10m));
}

/// <summary>Why an order has no automatic rate: a stable code plus the line or seller it concerns.</summary>
public sealed record QuoteReason(string Code, Guid? VariantId, Guid? SellerId);

/// <summary>One seller's parcel group. <see cref="ShippingMinor"/> is what <c>SellerTerms</c> takes.</summary>
public sealed record SellerShippingFee(Guid SellerId, long ShippingMinor, long ChargeableGrams);

public abstract record ShippingQuote
{
    /// <summary>Automatic rate. <see cref="TotalMinor"/> is exactly the sum of the per-seller fees.</summary>
    public sealed record Priced(Guid ZoneId, IReadOnlyList<SellerShippingFee> PerSeller, long TotalMinor, bool FreeShippingApplied) : ShippingQuote;

    /// <summary>Heavy, oversize or beyond the last weight band: staff must quote by hand.</summary>
    public sealed record QuoteOnly(IReadOnlyList<QuoteReason> Reasons) : ShippingQuote;

    /// <summary>No active zone serves the destination district.</summary>
    public sealed record NoZone : ShippingQuote;
}

/// <summary>
/// Shipping fee per seller from the destination zone's tariff (brief §8.9). Pure and deterministic. One parcel group per seller:
/// the seller's total weight (whole grams, partial grams already rounded up) picks the first band that holds it.
/// </summary>
public static class ShippingCalculator
{
    public const string NoWeight = "NO_WEIGHT";
    public const string HeavyUnit = "HEAVY_ITEM";
    public const string Oversize = "OVERSIZE_ITEM";
    public const string AboveLastBand = "ABOVE_LAST_BAND";

    /// <exception cref="ValidationException">A line has no usable weight or quantity (never silently free).</exception>
    public static ShippingQuote Calculate(IReadOnlyList<ShippingZoneConfig> zones, Guid destinationDistrictId, IReadOnlyList<ShippingLine> lines, long orderSubtotalMinor)
    {
        if (lines.Count == 0) throw new ValidationException("lines", "An order needs at least one line.");
        if (orderSubtotalMinor < 0) throw new ValidationException("subtotal", "The subtotal cannot be negative.");
        foreach (var l in lines)
        {
            if (l.Quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
            if (l.UnitGrams <= 0) throw new ValidationException("weight", $"{NoWeight}: variant {l.VariantId} has no weight.");
        }

        var matches = zones.Where(z => z.DistrictIds.Contains(destinationDistrictId)).Take(2).ToList();
        if (matches.Count == 0) return new ShippingQuote.NoZone();
        if (matches.Count > 1) throw new ValidationException("zones", "A district is in more than one zone.");
        var zone = matches[0];
        var rates = zone.Rates;

        var reasons = new List<QuoteReason>();
        foreach (var l in lines)
        {
            if (rates.HeavyUnitGrams is { } heavy && l.UnitGrams > heavy) reasons.Add(new QuoteReason(HeavyUnit, l.VariantId, l.SellerId));
            if (rates.OversizeDimensionMm is { } max && (l.LengthMm > max || l.WidthMm > max || l.HeightMm > max))
                reasons.Add(new QuoteReason(Oversize, l.VariantId, l.SellerId));
        }

        var free = rates.FreeShippingThresholdMinor is { } threshold && orderSubtotalMinor >= threshold;
        var perSeller = new List<SellerShippingFee>();
        foreach (var group in lines.GroupBy(l => l.SellerId)) // first-appearance order: independent of hashing
        {
            var grams = group.Sum(l => (long)l.Quantity * l.UnitGrams);
            var band = rates.Bands.FirstOrDefault(b => grams <= b.MaxGrams);
            if (band is null)
            {
                reasons.Add(new QuoteReason(AboveLastBand, null, group.Key));
                continue;
            }

            var fee = free ? 0L : Math.Min(band.FeeMinor, rates.MaxFeeMinor);
            perSeller.Add(new SellerShippingFee(group.Key, fee, grams));
        }

        if (reasons.Count > 0) return new ShippingQuote.QuoteOnly(reasons);
        return new ShippingQuote.Priced(zone.ZoneId, perSeller, perSeller.Sum(s => s.ShippingMinor), free);
    }
}
