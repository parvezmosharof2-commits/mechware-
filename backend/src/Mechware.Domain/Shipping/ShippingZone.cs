using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Shipping;

/// <summary>A district served by a zone. A district belongs to at most one zone (primary key in the database).</summary>
public sealed class ShippingZoneDistrict
{
    public Guid ZoneId { get; private set; }
    public Guid DistrictId { get; private set; }
    private ShippingZoneDistrict() { }
    internal ShippingZoneDistrict(Guid zoneId, Guid districtId) { ZoneId = zoneId; DistrictId = districtId; }
}

/// <summary>Stored form of <see cref="WeightBand"/>.</summary>
public sealed class ShippingRateBand
{
    public Guid ZoneId { get; private set; }
    public int MaxGrams { get; private set; }
    public long FeeMinor { get; private set; }
    private ShippingRateBand() { }
    internal ShippingRateBand(Guid zoneId, int maxGrams, long feeMinor) { ZoneId = zoneId; MaxGrams = maxGrams; FeeMinor = feeMinor; }
}

/// <summary>An admin-managed shipping zone with its rate card (editor comes in E8.4). Tariffs come from the owner/courier, never from code.</summary>
public sealed class ShippingZone : Entity<Guid>
{
    private readonly List<ShippingZoneDistrict> _districts = [];
    private readonly List<ShippingRateBand> _bands = [];

    public string Code { get; private set; } = string.Empty;
    public string NameEn { get; private set; } = string.Empty;
    public string NameBn { get; private set; } = string.Empty;
    public bool IsActive { get; private set; }
    public long? FreeShippingThresholdMinor { get; private set; }
    public int? HeavyUnitGrams { get; private set; }
    public int? OversizeDimensionMm { get; private set; }
    public long MaxFeeMinor { get; private set; }

    public IReadOnlyCollection<ShippingZoneDistrict> Districts => _districts;
    public IReadOnlyCollection<ShippingRateBand> Bands => _bands;

    private ShippingZone() { }

    public ShippingZone(Guid id, string code, string nameEn, string nameBn, IEnumerable<Guid> districtIds, ShippingRateCard rates, bool isActive = true) : base(id)
    {
        if (string.IsNullOrWhiteSpace(code) || code.Length > 40) throw new ValidationException("code", "A zone code is 1 to 40 characters.");
        if (string.IsNullOrWhiteSpace(nameEn) || string.IsNullOrWhiteSpace(nameBn)) throw new ValidationException("name", "A zone needs an English and a Bangla name.");
        Code = code.Trim().ToLowerInvariant();
        NameEn = nameEn.Trim();
        NameBn = nameBn.Trim();
        IsActive = isActive;
        SetRates(rates);

        foreach (var d in districtIds)
        {
            if (_districts.Any(x => x.DistrictId == d)) throw new ValidationException("districts", "A district is listed twice in the zone.");
            _districts.Add(new ShippingZoneDistrict(id, d));
        }
    }

    public void SetRates(ShippingRateCard rates)
    {
        FreeShippingThresholdMinor = rates.FreeShippingThresholdMinor;
        HeavyUnitGrams = rates.HeavyUnitGrams;
        OversizeDimensionMm = rates.OversizeDimensionMm;
        MaxFeeMinor = rates.MaxFeeMinor;
        _bands.Clear();
        _bands.AddRange(rates.Bands.Select(b => new ShippingRateBand(Id, b.MaxGrams, b.FeeMinor)));
        UpdatedAtUtc = DateTime.UtcNow;
    }

    public ShippingZoneConfig ToConfig() => new(Id, Code, _districts.Select(d => d.DistrictId).ToHashSet(),
        new ShippingRateCard(_bands.Select(b => new WeightBand(b.MaxGrams, b.FeeMinor)).ToList(),
            FreeShippingThresholdMinor, HeavyUnitGrams, OversizeDimensionMm, MaxFeeMinor));
}
