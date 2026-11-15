namespace Mechware.Application.Shipping;

// Staff API for shipping zones and rate cards (E8.4a). Request bodies carry no id, version of the server's making, audit field
// or status other than the zone's own on/off switch: anything else in a body is ignored. Money is whole poisha, weight whole
// grams, sides whole millimetres.

/// <summary>Hard sanity limits (a typo such as an extra zero is refused with a field error, never stored).</summary>
public static class ShippingZoneLimits
{
    public const int MaxBandGrams = 1_000_000;          // 1,000 kg
    public const int MaxBands = 20;
    public const long MaxFeeMinor = 10_000_000;         // 100,000.00 BDT
    public const long MaxFreeThresholdMinor = 10_000_000_000; // 100,000,000.00 BDT
    public const int MaxHeavyUnitGrams = 1_000_000;
    public const int MaxOversizeMm = 20_000;            // 20 m
    public const int MaxDistricts = 100;
    public const int MaxPreviewLines = 200;
    public const int MaxPreviewQuantity = 100_000;
}

public sealed record ShippingBandDto(int MaxGrams, long FeeMinor);

/// <summary>The tariff. <c>Bands</c>: up to <c>MaxGrams</c> (inclusive) costs <c>FeeMinor</c>; heavier than the last band is quote-only.</summary>
public sealed record ShippingRatesDto(
    IReadOnlyList<ShippingBandDto>? Bands, long? FreeShippingThresholdMinor, int? HeavyUnitGrams, int? OversizeDimensionMm, long MaxFeeMinor);

/// <param name="Code">Create only (lower-cased, 1 to 40 of a-z, 0-9, dash, underscore). On update it may be omitted or must equal the stored code.</param>
/// <param name="ExpectedVersion">Update only, required: the <c>version</c> the editor loaded; a stale one is a 409.</param>
public sealed record ShippingZoneRequest(
    string? Code, string? NameEn, string? NameBn, bool IsActive, IReadOnlyList<Guid>? DistrictIds, ShippingRatesDto? Rates, long? ExpectedVersion);

public sealed record ShippingZoneSummaryDto(Guid Id, string Code, string NameEn, string NameBn, bool IsActive, int DistrictCount, int BandCount, long Version);

public sealed record ShippingDistrictDto(Guid Id, string NameEn, string NameBn);

public sealed record ShippingZoneDetailDto(
    Guid Id, string Code, string NameEn, string NameBn, bool IsActive, long Version,
    IReadOnlyList<ShippingDistrictDto> Districts, ShippingRatesDto Rates, DateTime CreatedAtUtc, DateTime? UpdatedAtUtc);

public sealed record ShippingPreviewLine(Guid? SellerId, int Quantity, int UnitWeightGrams, int? LengthMm, int? WidthMm, int? HeightMm);

public sealed record ShippingPreviewRequest(Guid DistrictId, IReadOnlyList<ShippingPreviewLine>? Lines, long SubtotalMinor);

public sealed record ShippingPreviewSellerFee(Guid SellerId, long ShippingMinor, long ChargeableGrams);

/// <summary>Why a hypothetical order is quote-only: calculator code plus the index of the request line (or seller) it concerns.</summary>
public sealed record ShippingPreviewReason(string Code, int? LineIndex, Guid? SellerId);

/// <param name="Outcome">Priced, QuoteOnly or NoZone, exactly as the calculator decides it against the active zones.</param>
public sealed record ShippingPreviewDto(
    string Outcome, Guid? ZoneId, string? ZoneCode, IReadOnlyList<ShippingPreviewSellerFee> PerSeller, long TotalMinor,
    bool FreeShippingApplied, IReadOnlyList<ShippingPreviewReason> Reasons);

public sealed record ShippingCoverageDistrictDto(Guid DistrictId, string NameEn, string NameBn, string? DivisionNameEn, Guid? ZoneId, string? ZoneCode, bool ZoneActive);

/// <param name="Uncovered">Districts no ACTIVE zone serves (orders to them are refused in production). <c>ZoneCode</c> is set when an inactive zone still holds the district.</param>
public sealed record ShippingCoverageDto(int ActiveZoneCount, IReadOnlyList<ShippingCoverageDistrictDto> Covered, IReadOnlyList<ShippingCoverageDistrictDto> Uncovered);

public interface IShippingZoneService
{
    Task<IReadOnlyList<ShippingZoneSummaryDto>> ListAsync(CancellationToken ct = default);
    Task<ShippingZoneDetailDto> GetAsync(Guid id, CancellationToken ct = default);
    Task<ShippingZoneDetailDto> CreateAsync(ShippingZoneRequest request, CancellationToken ct = default);
    Task<ShippingZoneDetailDto> UpdateAsync(Guid id, ShippingZoneRequest request, CancellationToken ct = default);
    Task DeleteAsync(Guid id, CancellationToken ct = default);
    Task<ShippingPreviewDto> PreviewAsync(ShippingPreviewRequest request, CancellationToken ct = default);
    Task<ShippingCoverageDto> CoverageAsync(CancellationToken ct = default);
}
