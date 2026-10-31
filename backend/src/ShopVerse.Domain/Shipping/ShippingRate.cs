using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Shipping;

/// <summary>
/// A zone's tariff as staff configure it. There are deliberately no default rows: the numbers are a business decision, and until
/// a row exists for a zone, shipping to it is not priced (the order is refused rather than given a guessed fee).
/// </summary>
public sealed class ShippingRate : Entity<Guid>
{
    public ShippingZone Zone { get; private set; }
    public long BaseMinor { get; private set; }
    public long PerKgMinor { get; private set; }
    public int VolumetricDivisor { get; private set; } = 5000;
    public int WeightStepGrams { get; private set; } = 500;
    public int? MaxParcelGrams { get; private set; }
    public int? MaxDimensionMm { get; private set; }

    private ShippingRate() { }

    public ShippingRate(Guid id, ShippingZone zone, long baseMinor, long perKgMinor, int volumetricDivisor = 5000,
        int weightStepGrams = 500, int? maxParcelGrams = null, int? maxDimensionMm = null) : base(id)
    {
        if (baseMinor < 0 || perKgMinor < 0) throw new ValidationException("rate", "A rate cannot be negative.");
        if (volumetricDivisor <= 0 || weightStepGrams <= 0) throw new ValidationException("rate", "The divisor and weight step must be positive.");
        if (maxParcelGrams is <= 0 || maxDimensionMm is <= 0) throw new ValidationException("limit", "A limit must be positive.");
        Zone = zone;
        BaseMinor = baseMinor;
        PerKgMinor = perKgMinor;
        VolumetricDivisor = volumetricDivisor;
        WeightStepGrams = weightStepGrams;
        MaxParcelGrams = maxParcelGrams;
        MaxDimensionMm = maxDimensionMm;
    }

    public ShippingRateCard ToCard() => new(Zone, BaseMinor, PerKgMinor, VolumetricDivisor, WeightStepGrams, MaxParcelGrams, MaxDimensionMm);

    /// <summary>Maps the location table's text (inside_dhaka, dhaka_suburb or dhaka_suburbs, outside_dhaka); anything else is the dearest zone, never the cheapest.</summary>
    public static ShippingZone ZoneFromLocation(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "inside_dhaka" => ShippingZone.InsideDhaka,
        "dhaka_suburb" or "dhaka_suburbs" => ShippingZone.DhakaSuburbs,
        _ => ShippingZone.OutsideDhaka
    };
}
