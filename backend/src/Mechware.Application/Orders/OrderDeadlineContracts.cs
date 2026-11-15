using Mechware.Application.Catalog;

namespace Mechware.Application.Orders;

/// <summary>
/// An order past a legal deadline (E8.3). Staff-facing and deliberately free of personal data: no name, phone or street, only
/// the order number, status, destination district and the deadlines.
/// </summary>
public sealed record OverdueOrderDto(
    Guid OrderId, string OrderNumber, string Status, Guid? DistrictId, string? District,
    DateTime LegalDeliveryDeadlineUtc, DateTime CourierHandoverDeadlineUtc,
    bool DeliveryOverdue, bool HandoverOverdue, double HoursOverdue);

/// <summary>Fixes the legal deadlines of an order that is being confirmed now (E8.3); every confirmation path asks this one place.</summary>
public interface IDeliveryDeadlineCalculator
{
    Task<Mechware.Domain.Orders.DeliveryDeadlines> ForConfirmationAsync(
        string shipToJson, IReadOnlyCollection<Guid> sellerIds, DateTime confirmedAtUtc, CancellationToken ct = default);
}

public interface IOrderDeadlineService
{
    /// <summary>
    /// Orders past a deadline at <paramref name="asOfUtc"/>: past the legal delivery deadline and not yet delivered, or past the
    /// courier handover deadline and not yet shipped. An order is overdue strictly after the deadline instant (at the instant it is
    /// still on time). Cancelled, refunded, returned and delivered orders are never listed. Most overdue first; page size at most 50.
    /// </summary>
    Task<PagedResult<OverdueOrderDto>> ListOverdueAsync(DateTime asOfUtc, int page, int pageSize = 25, CancellationToken ct = default);
}
