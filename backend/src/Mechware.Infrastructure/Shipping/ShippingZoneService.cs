using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Shipping;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Shipping;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Shipping;

/// <summary>
/// Staff administration of shipping zones (E8.4a). The rules live in <see cref="ShippingRateCard"/> and <see cref="ShippingZone"/>;
/// this class adds the sanity limits, the "district in only one zone" and "district exists" checks with readable errors, an
/// <c>xmin</c>-based version for whole-document updates, and an audit row per change. The author of a change is always the
/// signed-in user. Preview reads and writes nothing.
/// </summary>
public sealed partial class ShippingZoneService(MechwareDbContext db, IShippingRateProvider rates, ICurrentUserService user, TimeProvider clock) : IShippingZoneService
{
    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Sign in to manage shipping zones.");

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,39}$")]
    private static partial Regex CodePattern();

    private sealed class VersionRow { public Guid Id { get; set; } public long Version { get; set; } }

    public async Task<IReadOnlyList<ShippingZoneSummaryDto>> ListAsync(CancellationToken ct = default)
    {
        var rows = await db.ShippingZones.AsNoTracking().OrderBy(z => z.Code)
            .Select(z => new { z.Id, z.Code, z.NameEn, z.NameBn, z.IsActive, Districts = z.Districts.Count, Bands = z.Bands.Count }).ToListAsync(ct);
        var versions = (await db.Database.SqlQuery<VersionRow>($"SELECT id AS \"Id\", xmin::text::bigint AS \"Version\" FROM shipping_zones").ToListAsync(ct))
            .ToDictionary(v => v.Id, v => v.Version);
        return rows.Select(r => new ShippingZoneSummaryDto(r.Id, r.Code, r.NameEn, r.NameBn, r.IsActive, r.Districts, r.Bands, versions.GetValueOrDefault(r.Id))).ToList();
    }

    public async Task<ShippingZoneDetailDto> GetAsync(Guid id, CancellationToken ct = default) =>
        await DetailAsync(await LoadAsync(id, tracked: false, ct), ct);

    public async Task<ShippingZoneDetailDto> CreateAsync(ShippingZoneRequest request, CancellationToken ct = default)
    {
        var (nameEn, nameBn, districts, card) = Validate(request);
        var code = (request.Code ?? string.Empty).Trim().ToLowerInvariant();
        var errors = new List<ValidationError>();
        if (!CodePattern().IsMatch(code)) errors.Add(new("code", "The code is 1 to 40 characters: letters a-z, digits, dash or underscore."));
        if (errors.Count > 0) throw new ValidationException(errors);

        var actor = Actor;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        if (await db.ShippingZones.AnyAsync(z => z.Code == code, ct))
            throw new ConflictException("SHIPPING_ZONE_CODE_TAKEN", $"A zone with the code '{code}' already exists.");
        await CheckDistrictsAsync(districts, exceptZone: null, ct);

        var zone = new ShippingZone(Guid.NewGuid(), code, nameEn, nameBn, districts, card, request.IsActive);
        db.ShippingZones.Add(zone);
        db.AuditLogs.Add(Audit("shipping.zone.created", zone.Id, null, Summary(zone), actor));
        await SaveAsync(ct);
        await tx.CommitAsync(ct);
        return await DetailAsync(zone, ct);
    }

    public async Task<ShippingZoneDetailDto> UpdateAsync(Guid id, ShippingZoneRequest request, CancellationToken ct = default)
    {
        var (nameEn, nameBn, districts, card) = Validate(request);
        if (request.ExpectedVersion is not { } expected) throw new ValidationException("expectedVersion", "Send the version you loaded (expectedVersion).");
        var actor = Actor;

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var current = await db.Database.SqlQuery<long>($"SELECT xmin::text::bigint AS \"Value\" FROM shipping_zones WHERE id = {id} FOR UPDATE").ToListAsync(ct);
        if (current.Count == 0) throw new KeyNotFoundException("Shipping zone not found.");
        if (current[0] != expected)
            throw new ConflictException("SHIPPING_ZONE_STALE", "This zone was changed by someone else since you opened it. Reload it and apply your change again.");

        var zone = await LoadAsync(id, tracked: true, ct);
        if (!string.IsNullOrWhiteSpace(request.Code) && !string.Equals(request.Code.Trim(), zone.Code, StringComparison.OrdinalIgnoreCase))
            throw new ValidationException("code", "The code of a zone cannot be changed.");
        await CheckDistrictsAsync(districts, exceptZone: id, ct);

        var before = Summary(zone);
        zone.Edit(nameEn, nameBn, request.IsActive);
        zone.SetRates(card);
        zone.SetDistricts(districts);
        db.AuditLogs.Add(Audit("shipping.zone.updated", id, before, Summary(zone), actor));
        await SaveAsync(ct);
        var detail = await DetailAsync(zone, ct);
        await tx.CommitAsync(ct);
        return detail;
    }

    public async Task DeleteAsync(Guid id, CancellationToken ct = default)
    {
        var actor = Actor;
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = await db.Database.SqlQuery<Guid>($"SELECT id AS \"Value\" FROM shipping_zones WHERE id = {id} FOR UPDATE").ToListAsync(ct);
        if (locked.Count == 0) throw new KeyNotFoundException("Shipping zone not found.");
        var zone = await LoadAsync(id, tracked: false, ct);
        if (zone.IsActive) throw new ConflictException("SHIPPING_ZONE_ACTIVE", "Deactivate the zone before deleting it.");

        // The database cascades to the districts and bands. Shipping is priced live from the zones, so no stored order refers to one.
        await db.ShippingZones.Where(z => z.Id == id).ExecuteDeleteAsync(ct);
        db.AuditLogs.Add(Audit("shipping.zone.deleted", id, Summary(zone), null, actor));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task<ShippingPreviewDto> PreviewAsync(ShippingPreviewRequest request, CancellationToken ct = default)
    {
        var errors = new List<ValidationError>();
        var items = request.Lines ?? [];
        if (items.Count is 0 or > ShippingZoneLimits.MaxPreviewLines)
            errors.Add(new("lines", $"Send 1 to {ShippingZoneLimits.MaxPreviewLines} lines."));
        if (request.SubtotalMinor < 0) errors.Add(new("subtotalMinor", "The subtotal cannot be negative."));
        for (var i = 0; i < items.Count && errors.Count < 20; i++)
        {
            var l = items[i];
            if (l.Quantity is <= 0 or > ShippingZoneLimits.MaxPreviewQuantity) errors.Add(new($"lines[{i}].quantity", $"Quantity is 1 to {ShippingZoneLimits.MaxPreviewQuantity}."));
            if (l.UnitWeightGrams is <= 0 or > ShippingZoneLimits.MaxBandGrams) errors.Add(new($"lines[{i}].unitWeightGrams", $"Unit weight is 1 to {ShippingZoneLimits.MaxBandGrams} grams."));
            foreach (var (name, side) in new[] { ("lengthMm", l.LengthMm), ("widthMm", l.WidthMm), ("heightMm", l.HeightMm) })
                if (side is <= 0 or > ShippingZoneLimits.MaxOversizeMm * 10) errors.Add(new($"lines[{i}].{name}", "A side must be a positive number of millimetres."));
        }

        if (errors.Count > 0) throw new ValidationException(errors);

        var lines = items.Select(l => new ShippingLine(Guid.NewGuid(), l.SellerId ?? Guid.Empty, l.Quantity, l.UnitWeightGrams, l.LengthMm, l.WidthMm, l.HeightMm)).ToList();
        var zones = await rates.GetActiveZonesAsync(ct);
        switch (ShippingCalculator.Calculate(zones, request.DistrictId, lines, request.SubtotalMinor))
        {
            case ShippingQuote.Priced p:
                return new ShippingPreviewDto("Priced", p.ZoneId, zones.First(z => z.ZoneId == p.ZoneId).Code,
                    p.PerSeller.Select(s => new ShippingPreviewSellerFee(s.SellerId, s.ShippingMinor, s.ChargeableGrams)).ToList(), p.TotalMinor, p.FreeShippingApplied, []);
            case ShippingQuote.QuoteOnly q:
                var index = lines.Select((l, i) => (l.VariantId, i)).ToDictionary(x => x.VariantId, x => x.i);
                return new ShippingPreviewDto("QuoteOnly", null, null, [], 0, false,
                    q.Reasons.Select(r => new ShippingPreviewReason(r.Code, r.VariantId is { } v ? index[v] : null, r.SellerId)).ToList());
            default:
                return new ShippingPreviewDto("NoZone", null, null, [], 0, false, []);
        }
    }

    public async Task<ShippingCoverageDto> CoverageAsync(CancellationToken ct = default)
    {
        var districts = await db.BdLocations.AsNoTracking().Where(l => l.Level == "district")
            .Select(l => new { l.Id, l.NameEn, l.NameBn, l.ParentId }).ToListAsync(ct);
        var divisions = await db.BdLocations.AsNoTracking().Where(l => l.Level == "division").ToDictionaryAsync(l => l.Id, l => l.NameEn, ct);
        var owners = (await db.ShippingZones.AsNoTracking().SelectMany(z => z.Districts.Select(d => new { d.DistrictId, ZoneId = z.Id, z.Code, z.IsActive })).ToListAsync(ct))
            .ToDictionary(o => o.DistrictId);

        var all = districts.OrderBy(d => d.NameEn).Select(d =>
        {
            owners.TryGetValue(d.Id, out var o);
            return new ShippingCoverageDistrictDto(d.Id, d.NameEn, d.NameBn, d.ParentId is { } p ? divisions.GetValueOrDefault(p) : null, o?.ZoneId, o?.Code, o?.IsActive ?? false);
        }).ToList();
        return new ShippingCoverageDto(await db.ShippingZones.CountAsync(z => z.IsActive, ct), all.Where(d => d.ZoneActive).ToList(), all.Where(d => !d.ZoneActive).ToList());
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    /// <summary>Sanity limits and shape; the tariff rules themselves (ascending bands, fees within the cap) are the domain's.</summary>
    private static (string NameEn, string NameBn, List<Guid> Districts, ShippingRateCard Card) Validate(ShippingZoneRequest r)
    {
        var errors = new List<ValidationError>();
        var nameEn = (r.NameEn ?? string.Empty).Trim();
        var nameBn = (r.NameBn ?? string.Empty).Trim();
        if (nameEn.Length is 0 or > 100) errors.Add(new("nameEn", "The English name is 1 to 100 characters."));
        if (nameBn.Length is 0 or > 100) errors.Add(new("nameBn", "The Bangla name is 1 to 100 characters."));

        var districts = (r.DistrictIds ?? []).ToList();
        if (districts.Count > ShippingZoneLimits.MaxDistricts) errors.Add(new("districtIds", $"At most {ShippingZoneLimits.MaxDistricts} districts per zone."));
        if (districts.Distinct().Count() != districts.Count) errors.Add(new("districtIds", "A district is listed twice."));
        if (districts.Contains(Guid.Empty)) errors.Add(new("districtIds", "A district id is empty."));

        var rates = r.Rates;
        if (rates is null) errors.Add(new("rates", "Send the rate card."));
        else
        {
            var bands = rates.Bands ?? [];
            if (bands.Count is 0 or > ShippingZoneLimits.MaxBands) errors.Add(new("rates.bands", $"A rate card has 1 to {ShippingZoneLimits.MaxBands} weight bands."));
            for (var i = 0; i < bands.Count && i < ShippingZoneLimits.MaxBands; i++)
            {
                if (bands[i].MaxGrams is <= 0 or > ShippingZoneLimits.MaxBandGrams) errors.Add(new($"rates.bands[{i}].maxGrams", $"A band's upper weight is 1 to {ShippingZoneLimits.MaxBandGrams} grams."));
                if (bands[i].FeeMinor < 0 || bands[i].FeeMinor > ShippingZoneLimits.MaxFeeMinor) errors.Add(new($"rates.bands[{i}].feeMinor", $"A band's fee is 0 to {ShippingZoneLimits.MaxFeeMinor} poisha."));
            }

            if (rates.MaxFeeMinor is <= 0 or > ShippingZoneLimits.MaxFeeMinor) errors.Add(new("rates.maxFeeMinor", $"The fee cap is 1 to {ShippingZoneLimits.MaxFeeMinor} poisha."));
            if (rates.FreeShippingThresholdMinor is <= 0 or > ShippingZoneLimits.MaxFreeThresholdMinor) errors.Add(new("rates.freeShippingThresholdMinor", $"The free-shipping threshold is 1 to {ShippingZoneLimits.MaxFreeThresholdMinor} poisha, or empty."));
            if (rates.HeavyUnitGrams is <= 0 or > ShippingZoneLimits.MaxHeavyUnitGrams) errors.Add(new("rates.heavyUnitGrams", $"The heavy-unit weight is 1 to {ShippingZoneLimits.MaxHeavyUnitGrams} grams, or empty."));
            if (rates.OversizeDimensionMm is <= 0 or > ShippingZoneLimits.MaxOversizeMm) errors.Add(new("rates.oversizeDimensionMm", $"The oversize side is 1 to {ShippingZoneLimits.MaxOversizeMm} mm, or empty."));
        }

        if (errors.Count > 0) throw new ValidationException(errors);
        var card = new ShippingRateCard(rates!.Bands!.Select(b => new WeightBand(b.MaxGrams, b.FeeMinor)).ToList(),
            rates.FreeShippingThresholdMinor, rates.HeavyUnitGrams, rates.OversizeDimensionMm, rates.MaxFeeMinor);
        return (nameEn, nameBn, districts, card);
    }

    /// <summary>Every id must be a district; none may already belong to another zone (active or not: a district has one owner).</summary>
    private async Task CheckDistrictsAsync(IReadOnlyCollection<Guid> ids, Guid? exceptZone, CancellationToken ct)
    {
        if (ids.Count == 0) return;
        var known = await db.BdLocations.AsNoTracking().Where(l => ids.Contains(l.Id) && l.Level == "district").Select(l => new { l.Id, l.NameEn }).ToListAsync(ct);
        var unknown = ids.Except(known.Select(k => k.Id)).ToList();
        if (unknown.Count > 0)
            throw new ValidationException(unknown.Select(u => new ValidationError("districtIds", $"{u} is not a district.")));

        var clashes = await db.ShippingZones.AsNoTracking().Where(z => z.Id != exceptZone)
            .SelectMany(z => z.Districts.Where(d => ids.Contains(d.DistrictId)).Select(d => new { d.DistrictId, z.Code, z.NameEn, z.IsActive })).ToListAsync(ct);
        if (clashes.Count > 0)
        {
            var names = known.ToDictionary(k => k.Id, k => k.NameEn);
            throw new ConflictException("SHIPPING_DISTRICT_ALREADY_IN_ZONE",
                string.Join(" ", clashes.Select(c => $"District '{names[c.DistrictId]}' ({c.DistrictId}) is already in zone '{c.Code}' ({c.NameEn}, {(c.IsActive ? "active" : "inactive")}). Remove it there first.")));
        }
    }

    private async Task SaveAsync(CancellationToken ct)
    {
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw new ConflictException("SHIPPING_ZONE_CONFLICT", "Another change took this code or one of these districts at the same time. Reload and try again.");
        }
    }

    private async Task<ShippingZone> LoadAsync(Guid id, bool tracked, CancellationToken ct)
    {
        var q = tracked ? db.ShippingZones : db.ShippingZones.AsNoTracking();
        return await q.Include(z => z.Districts).Include(z => z.Bands).AsSplitQuery().SingleOrDefaultAsync(z => z.Id == id, ct)
            ?? throw new KeyNotFoundException("Shipping zone not found.");
    }

    private async Task<ShippingZoneDetailDto> DetailAsync(ShippingZone zone, CancellationToken ct)
    {
        var ids = zone.Districts.Select(d => d.DistrictId).ToList();
        var districts = await db.BdLocations.AsNoTracking().Where(l => ids.Contains(l.Id)).OrderBy(l => l.NameEn)
            .Select(l => new ShippingDistrictDto(l.Id, l.NameEn, l.NameBn)).ToListAsync(ct);
        var version = (await db.Database.SqlQuery<long>($"SELECT xmin::text::bigint AS \"Value\" FROM shipping_zones WHERE id = {zone.Id}").ToListAsync(ct)).Single();
        return new ShippingZoneDetailDto(zone.Id, zone.Code, zone.NameEn, zone.NameBn, zone.IsActive, version, districts,
            new ShippingRatesDto(zone.Bands.OrderBy(b => b.MaxGrams).Select(b => new ShippingBandDto(b.MaxGrams, b.FeeMinor)).ToList(),
                zone.FreeShippingThresholdMinor, zone.HeavyUnitGrams, zone.OversizeDimensionMm, zone.MaxFeeMinor),
            zone.CreatedAtUtc, zone.UpdatedAtUtc);
    }

    private static string Summary(ShippingZone z) => JsonSerializer.Serialize(new
    {
        z.Code,
        z.IsActive,
        Districts = z.Districts.Select(d => d.DistrictId).Order().ToList(),
        Bands = z.Bands.OrderBy(b => b.MaxGrams).Select(b => new { b.MaxGrams, b.FeeMinor }).ToList(),
        z.MaxFeeMinor,
        z.FreeShippingThresholdMinor,
        z.HeavyUnitGrams,
        z.OversizeDimensionMm,
    });

    private AuditLog Audit(string action, Guid zoneId, string? oldValues, string? newValues, Guid actor) =>
        new(Guid.NewGuid(), actor, user.IpAddress, action, "shipping_zones", zoneId.ToString(), oldValues, newValues, user.CorrelationId, clock.GetUtcNow().UtcDateTime);
}
