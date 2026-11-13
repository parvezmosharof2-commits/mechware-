using Mechware.Domain.Common.Exceptions;

namespace Mechware.Domain.Shipping;

/// <summary>One weight band: a parcel up to <paramref name="MaxGrams"/> grams (inclusive) costs <paramref name="FeeMinor"/> poisha.</summary>
public sealed record WeightBand(int MaxGrams, long FeeMinor);

/// <summary>
/// A zone's tariff (brief §8.9). Weight is whole grams, money is whole poisha: no floating point anywhere.
/// A parcel heavier than the last band is quote-only. Fees never fall as weight rises, so a heavier parcel is never cheaper.
/// </summary>
public sealed record ShippingRateCard
{
    public IReadOnlyList<WeightBand> Bands { get; }

    /// <summary>Order subtotal at or above this ships free (null = never free).</summary>
    public long? FreeShippingThresholdMinor { get; }

    /// <summary>A single unit heavier than this makes the order quote-only (null = rule off).</summary>
    public int? HeavyUnitGrams { get; }

    /// <summary>Any side longer than this makes the order quote-only (null = rule off).</summary>
    public int? OversizeDimensionMm { get; }

    /// <summary>Sanity cap: a seller's fee is never above this.</summary>
    public long MaxFeeMinor { get; }

    public ShippingRateCard(IReadOnlyList<WeightBand> bands, long? freeShippingThresholdMinor, int? heavyUnitGrams, int? oversizeDimensionMm, long maxFeeMinor)
    {
        if (bands.Count == 0) throw new ValidationException("bands", "A rate card needs at least one weight band.");
        if (maxFeeMinor <= 0) throw new ValidationException("maxFee", "The fee cap must be positive.");
        if (freeShippingThresholdMinor is <= 0) throw new ValidationException("freeShippingThreshold", "The free-shipping threshold must be positive.");
        if (heavyUnitGrams is <= 0) throw new ValidationException("heavyUnitGrams", "The heavy-unit weight must be positive.");
        if (oversizeDimensionMm is <= 0) throw new ValidationException("oversizeDimensionMm", "The oversize dimension must be positive.");

        var sorted = bands.OrderBy(b => b.MaxGrams).ToList();
        for (var i = 0; i < sorted.Count; i++)
        {
            var b = sorted[i];
            if (b.MaxGrams <= 0) throw new ValidationException("bands", "A band's upper weight must be positive.");
            if (b.FeeMinor < 0) throw new ValidationException("bands", "A band's fee cannot be negative.");
            if (b.FeeMinor > maxFeeMinor) throw new ValidationException("bands", "A band's fee is above the fee cap.");
            if (i > 0 && b.MaxGrams == sorted[i - 1].MaxGrams) throw new ValidationException("bands", "Two bands have the same upper weight.");
            if (i > 0 && b.FeeMinor < sorted[i - 1].FeeMinor) throw new ValidationException("bands", "A heavier band cannot cost less than a lighter one.");
        }

        Bands = sorted;
        FreeShippingThresholdMinor = freeShippingThresholdMinor;
        HeavyUnitGrams = heavyUnitGrams;
        OversizeDimensionMm = oversizeDimensionMm;
        MaxFeeMinor = maxFeeMinor;
    }
}

/// <summary>A zone as the calculator sees it: the districts it serves and its tariff. Pure data, no entity tracking.</summary>
public sealed record ShippingZoneConfig(Guid ZoneId, string Code, IReadOnlySet<Guid> DistrictIds, ShippingRateCard Rates);
