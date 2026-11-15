using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Mechware.Application.Catalog;
using Mechware.Application.Orders;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// Legal delivery periods (E8.3). The numbers come from the brief and Guideline 2021 and are for the lawyer to confirm, so they are
/// configuration (<c>Delivery:SameCityDays</c>, <c>Delivery:OtherCityDays</c>, <c>Delivery:HandoverHours</c>), never constants in code.
/// A value that is not a whole number above 0 stops the host at startup.
/// </summary>
public sealed class DeliveryOptions
{
    public const string SectionName = "Delivery";
    public const int MaxDays = 365;
    public const int MaxHours = 24 * MaxDays;

    public int SameCityDays { get; init; } = 5;
    public int OtherCityDays { get; init; } = 10;
    public int HandoverHours { get; init; } = 48;

    public static DeliveryOptions From(IConfiguration configuration)
    {
        static int Read(IConfiguration c, string key, int fallback, int max)
        {
            var raw = c[$"{SectionName}:{key}"];
            if (string.IsNullOrWhiteSpace(raw)) return fallback;
            if (!int.TryParse(raw, out var value) || value <= 0 || value > max)
                throw new InvalidOperationException($"{SectionName}:{key} must be a whole number between 1 and {max}.");
            return value;
        }
        return new DeliveryOptions
        {
            SameCityDays = Read(configuration, nameof(SameCityDays), 5, MaxDays),
            OtherCityDays = Read(configuration, nameof(OtherCityDays), 10, MaxDays),
            HandoverHours = Read(configuration, nameof(HandoverHours), 48, MaxHours)
        };
    }

    public DeliveryDeadlines Compute(DateTime confirmedAtUtc, bool? sameCity) =>
        DeliveryDeadlines.Compute(confirmedAtUtc, sameCity, SameCityDays, OtherCityDays, HandoverHours);
}

/// <summary>The <see cref="IDeliveryDeadlineCalculator"/> over the database and the configured periods.</summary>
public sealed class DeliveryDeadlineCalculator(MechwareDbContext db, DeliveryOptions options) : IDeliveryDeadlineCalculator
{
    public Task<DeliveryDeadlines> ForConfirmationAsync(string shipToJson, IReadOnlyCollection<Guid> sellerIds, DateTime confirmedAtUtc, CancellationToken ct = default) =>
        OrderDeadlineService.ForConfirmationAsync(db, options, shipToJson, sellerIds, confirmedAtUtc, ct);
}

/// <summary>Works out the deadlines of an order about to be confirmed and answers the staff "overdue" question.</summary>
public sealed class OrderDeadlineService(MechwareDbContext db) : IOrderDeadlineService
{
    public const int MaxPageSize = 50;

    // Delivered, completed, cancelled, refunded, returned and return flows are never overdue. A failed delivery still is.
    private static readonly OrderStatus[] DeliveryOpen =
        [OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.Packed, OrderStatus.Shipped, OrderStatus.OutForDelivery, OrderStatus.DeliveryFailed];

    private static readonly OrderStatus[] HandoverOpen = [OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.Packed];

    /// <summary>
    /// "Same city" (E8.3, simple on purpose): the order's ship-to <c>districtId</c> equals the district of a dispatch warehouse of a
    /// seller in the order. A seller's dispatch warehouses are its own active warehouses, or the MECHWARE hub's when it has none
    /// (sellers have no district of their own). The strictest case wins across sellers: one same-city or unknown seller makes the
    /// whole order same city (order level, not per sub-order). Unknown (no ship-to district, no warehouse, district name not in the
    /// location list) returns null, which <see cref="DeliveryDeadlines"/> treats as the short period.
    /// </summary>
    public static async Task<bool?> ResolveSameCityAsync(MechwareDbContext db, string shipToJson, IReadOnlyCollection<Guid> sellerIdsOfOrder, CancellationToken ct)
    {
        var destination = DestinationDistrictId(shipToJson);
        if (destination is null) return null;

        var sellerIds = sellerIdsOfOrder.Distinct().ToList();
        var warehouses = await db.Warehouses.AsNoTracking()
            .Where(w => w.IsActive && (w.SellerId == null || sellerIds.Contains(w.SellerId.Value)))
            .Select(w => new { w.SellerId, w.District }).ToListAsync(ct);
        var names = warehouses.Select(w => w.District.Trim()).Where(n => n.Length > 0).Distinct().ToList();
        var lowered = names.Select(n => n.ToLowerInvariant()).ToList();
        var ids = await db.BdLocations.AsNoTracking()
            .Where(d => d.Level == "district" && (lowered.Contains(d.NameEn.ToLower()) || names.Contains(d.NameBn)))
            .Select(d => new { d.Id, d.NameEn, d.NameBn }).ToListAsync(ct);

        bool unknown = false, same = false;
        foreach (var sellerId in sellerIds)
        {
            var own = warehouses.Where(w => w.SellerId == sellerId).ToList();
            var dispatch = own.Count > 0 ? own : warehouses.Where(w => w.SellerId == null).ToList();
            var districtIds = dispatch.SelectMany(w => ids
                    .Where(d => d.NameEn.Equals(w.District.Trim(), StringComparison.OrdinalIgnoreCase) || d.NameBn == w.District.Trim())
                    .Select(d => d.Id)).ToList();
            if (districtIds.Count == 0) unknown = true;
            else if (districtIds.Contains(destination.Value)) same = true;
        }
        return same ? true : unknown ? null : false;
    }

    /// <summary>The deadlines for an order being confirmed now (one place; every confirmation path calls it).</summary>
    public static async Task<DeliveryDeadlines> ForConfirmationAsync(
        MechwareDbContext db, DeliveryOptions options, string shipToJson, IReadOnlyCollection<Guid> sellerIds, DateTime nowUtc, CancellationToken ct) =>
        options.Compute(nowUtc, await ResolveSameCityAsync(db, shipToJson, sellerIds, ct));

    private static Guid? DestinationDistrictId(string shipToJson) => ShipToDistrict(shipToJson).Id;

    public async Task<PagedResult<OverdueOrderDto>> ListOverdueAsync(DateTime asOfUtc, int page, int pageSize = 25, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var asOf = DateTime.SpecifyKind(asOfUtc, DateTimeKind.Utc);

        var query = db.Orders.AsNoTracking().Where(o =>
            (o.LegalDeliveryDeadlineUtc != null && o.LegalDeliveryDeadlineUtc < asOf && DeliveryOpen.Contains(o.Status)) ||
            (o.CourierHandoverDeadlineUtc != null && o.CourierHandoverDeadlineUtc < asOf && HandoverOpen.Contains(o.Status)));

        var total = await query.CountAsync(ct);
        var rows = await query.OrderBy(o => o.LegalDeliveryDeadlineUtc).ThenBy(o => o.Id)
            .Skip((page - 1) * pageSize).Take(pageSize)
            .Select(o => new { o.Id, o.OrderNumber, o.Status, o.ShipToJson, Legal = o.LegalDeliveryDeadlineUtc!.Value, Handover = o.CourierHandoverDeadlineUtc!.Value })
            .ToListAsync(ct);

        var items = rows.Select(r =>
        {
            var deliveryOverdue = r.Legal < asOf && DeliveryOpen.Contains(r.Status);
            var handoverOverdue = r.Handover < asOf && HandoverOpen.Contains(r.Status);
            var hours = Math.Max(deliveryOverdue ? (asOf - r.Legal).TotalHours : 0, handoverOverdue ? (asOf - r.Handover).TotalHours : 0);
            var (districtId, district) = ShipToDistrict(r.ShipToJson);
            return new OverdueOrderDto(r.Id, r.OrderNumber, OrderService.ToWire(r.Status), districtId, district, r.Legal, r.Handover,
                deliveryOverdue, handoverOverdue, Math.Round(hours, 2));
        }).ToList();
        return new PagedResult<OverdueOrderDto>(items, page, pageSize, total);
    }

    private static (Guid? Id, string? Name) ShipToDistrict(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            Guid? id = root.TryGetProperty("districtId", out var i) && i.ValueKind == JsonValueKind.String && i.TryGetGuid(out var g) ? g : null;
            var name = root.TryGetProperty("district", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            return (id, name);
        }
        catch (JsonException) { return (null, null); }
    }
}
