using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Domain.Shipping;

/// <summary>Where a parcel goes, as decided by the server from a saved address (never from text the client typed).</summary>
public enum ShippingZone
{
    InsideDhaka = 1,
    DhakaSuburbs = 2,
    OutsideDhaka = 3
}

/// <summary>One zone's tariff (brief §8.9): <c>base + perKg x chargeable weight</c>. All amounts in poisha.</summary>
/// <param name="VolumetricDivisor">cm³ per kg for volumetric weight (length x width x height / divisor). 5000 is the usual courier value; staff may change it.</param>
/// <param name="WeightStepGrams">The chargeable weight is rounded up to a multiple of this.</param>
/// <param name="MaxParcelGrams">Heavier parcels are quote-only; null means no weight limit.</param>
/// <param name="MaxDimensionMm">A longer side makes the parcel oversize and quote-only; null means no limit.</param>
public sealed record ShippingRateCard(
    ShippingZone Zone, long BaseMinor, long PerKgMinor, int VolumetricDivisor = 5000, int WeightStepGrams = 500,
    int? MaxParcelGrams = null, int? MaxDimensionMm = null);

/// <summary>One purchased line's physical size. Weights are per unit in grams, sides per unit in millimetres.</summary>
public sealed record ShippingItem(int Quantity, int WeightGrams, int LengthMm, int WidthMm, int HeightMm);

public sealed record ShippingQuote(long AmountMinor, int ChargeableGrams, bool QuoteRequired, string? Reason);

/// <summary>
/// Pure shipping fee for one seller's shipment. Chargeable weight is the larger of real and volumetric weight, summed over
/// the items and rounded up to the tariff's step; a parcel over the weight or dimension limit cannot be priced here and is
/// reported as quote-only instead of being guessed. Integer arithmetic throughout, so there is no rounding drift.
/// </summary>
public static class ShippingCalculator
{
    public const string TooHeavy = "TOO_HEAVY";
    public const string Oversize = "OVERSIZE";

    public static ShippingQuote Quote(ShippingRateCard card, IReadOnlyList<ShippingItem> items)
    {
        if (card.BaseMinor < 0 || card.PerKgMinor < 0) throw new ValidationException("rate", "A rate cannot be negative.");
        if (card.VolumetricDivisor <= 0 || card.WeightStepGrams <= 0) throw new ValidationException("rate", "The divisor and weight step must be positive.");
        if (items.Count == 0) throw new ValidationException("items", "A shipment needs at least one item.");

        long totalGrams = 0;
        foreach (var item in items)
        {
            if (item.Quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
            if (item.WeightGrams < 0 || item.LengthMm < 0 || item.WidthMm < 0 || item.HeightMm < 0)
                throw new ValidationException("size", "Weight and sides cannot be negative.");

            if (card.MaxDimensionMm is { } maxSide && Math.Max(item.LengthMm, Math.Max(item.WidthMm, item.HeightMm)) > maxSide)
                return new ShippingQuote(0, 0, true, Oversize);

            // volumetric kg = L x W x H (cm³) / divisor; in grams that is mm³ x 1000 / (1000 x divisor) = mm³ / divisor.
            var volumetricGrams = (long)item.LengthMm * item.WidthMm * item.HeightMm / card.VolumetricDivisor;
            totalGrams += checked(item.Quantity * Math.Max(item.WeightGrams, volumetricGrams));
        }

        if (card.MaxParcelGrams is { } maxGrams && totalGrams > maxGrams) return new ShippingQuote(0, 0, true, TooHeavy);

        var chargeable = (totalGrams + card.WeightStepGrams - 1) / card.WeightStepGrams * card.WeightStepGrams;
        var amount = card.BaseMinor + (chargeable * card.PerKgMinor + 500) / 1000;   // grams -> kg, rounded half up in poisha
        return new ShippingQuote(amount, (int)Math.Min(chargeable, int.MaxValue), false, null);
    }
}
