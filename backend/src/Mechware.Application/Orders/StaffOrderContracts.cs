using Mechware.Application.Catalog;

namespace Mechware.Application.Orders;

/// <summary>
/// Filters of the staff order list (G5.5). Every member is optional. <c>From</c>/<c>To</c> are Dhaka calendar dates (inclusive),
/// <c>Status</c> and <c>PaymentState</c> are wire names, <c>OrderNumber</c> is a prefix, <c>Overdue</c> true = past a legal deadline now.
/// </summary>
public sealed record StaffOrderFilter(
    string? Status, Guid? CompanyId, string? OrderNumber, string? From, string? To, string? PaymentState, bool? Overdue, int Page, int PageSize);

/// <summary>
/// One row of the staff list. <paramref name="PaymentState"/>: <c>none</c>, <c>on_credit</c> (company credit, no gateway payment),
/// <c>pending</c>, <c>captured</c>, <c>failed</c>, <c>needs_refund</c>, <c>refunded</c>. <paramref name="Source"/>: <c>cart</c> or <c>quote</c>.
/// No buyer name, phone or address.
/// </summary>
public sealed record StaffOrderListItemDto(
    Guid Id, string OrderNumber, string Status, Guid? CompanyId, string? CompanyName, long GrandTotalMinor, string PaymentState,
    DateTime PlacedAtUtc, DateTime? LegalDeliveryDeadlineUtc, DateTime? CourierHandoverDeadlineUtc, bool DeliveryOverdue, bool HandoverOverdue,
    string Source, string? QuoteNumber);

public sealed record StaffOrderBuyerDto(Guid UserId, string Name, string? Phone);

public sealed record StaffOrderQuoteDto(Guid QuoteId, string Number, Guid VersionId);

/// <summary>Where a line's unit price came from at the order's instant (ADR-0008 §8). No cost, floor or margin.</summary>
public sealed record StaffPriceSnapshotDto(
    string? Source, string? Basis, Guid? PriceListId, int? PriceListVersion, string? ContractRef, long? PublicUnitPriceMinor, Guid? QuoteLineId);

public sealed record StaffOrderLineDto(
    Guid VariantId, string Sku, string Title, int Quantity, long UnitPriceMinor, decimal VatRate, long GrossMinor, long DiscountMinor,
    long NetMinor, long TaxMinor, long TotalMinor, StaffPriceSnapshotDto PriceSnapshot);

public sealed record StaffSubOrderDto(
    Guid Id, string SubOrderNumber, Guid SellerId, string? StoreName, string Status, long ShippingMinor, long TotalMinor,
    IReadOnlyList<StaffOrderLineDto> Lines);

public sealed record StaffOrderEventDto(int Sequence, string FromStatus, string ToStatus, Guid? ActorId, string? Reason, DateTime AtUtc);

/// <summary>A refund of a payment. No provider reference.</summary>
public sealed record StaffRefundDto(
    Guid Id, long AmountMinor, string Status, string Reason, Guid RequestedBy, Guid? DecidedBy, DateTime? DecidedAtUtc, DateTime CreatedAtUtc);

/// <summary>One payment attempt: its channel (<paramref name="Method"/>), state and amount. No provider reference, note or card data.</summary>
public sealed record StaffPaymentDto(
    Guid Id, string Method, string Status, long AmountMinor, string Currency, DateTime CreatedAtUtc, DateTime? CapturedAtUtc,
    IReadOnlyList<StaffRefundDto> Refunds);

/// <summary>A credit order's standing in the company ledger (ADR-0010): <c>open</c>, <c>partially_paid</c>, <c>paid</c>, <c>overdue</c>, <c>cancelled</c>.</summary>
public sealed record StaffOrderCreditDto(string Status, DateOnly DueOn, long ChargedMinor, long OutstandingMinor);

/// <summary>A shipment as staff see it (F1.1): <paramref name="CreatedBy"/> is the staff id that shipped it (an id only, like event actors).</summary>
public sealed record StaffShipmentDto(
    Guid Id, string CourierName, string TrackingNumber, string? TrackingUrl, DateTime ShippedAtUtc, DateTime? DeliveredAtUtc, Guid CreatedBy);

/// <param name="RowVersion">The order's version: what the fulfilment moves (F1.1) send back; a stale one is 409 <c>ORDER_CHANGED</c>.</param>
public sealed record StaffOrderDetailDto(
    Guid Id, string OrderNumber, string Status, string Currency, string TaxMode, DateTime PlacedAtUtc, DateTime? ConfirmedAtUtc,
    DateTime? PaymentDueAtUtc, DateTime? LegalDeliveryDeadlineUtc, DateTime? CourierHandoverDeadlineUtc, bool? DeadlineSameCity,
    string? PurchaseOrderNumber, long SubtotalMinor, long DiscountMinor, long TaxMinor, long ShippingMinor, long GrandTotalMinor,
    string Source, StaffOrderQuoteDto? Quote, Guid? CompanyId, string? CompanyName, StaffOrderBuyerDto Buyer, OrderShipToDto? ShipTo,
    string PaymentState, bool PaysOnCredit, StaffOrderCreditDto? Credit,
    IReadOnlyList<StaffSubOrderDto> SubOrders, IReadOnlyList<StaffOrderEventDto> Events, IReadOnlyList<StaffPaymentDto> Payments,
    uint RowVersion, IReadOnlyList<StaffShipmentDto> Shipments, IReadOnlyList<string> InvoiceNumbers);

/// <summary>A fulfilment move: the order's <c>rowVersion</c> from the last read (required).</summary>
public sealed record OrderStepRequest(uint? RowVersion);

/// <summary>Ship: courier name (2-80, plain text), tracking number (1-60 letters, digits, dashes), optional https link on a known courier site.</summary>
public sealed record ShipOrderRequest(uint? RowVersion, string? CourierName, string? TrackingNumber, string? TrackingUrl);

/// <summary>The order after a fulfilment move: its new status and version, and its shipments.</summary>
public sealed record OrderFulfilmentDto(Guid Id, string OrderNumber, string Status, uint RowVersion, IReadOnlyList<StaffShipmentDto> Shipments,
    IReadOnlyList<string> InvoiceNumbers);

/// <summary>
/// Manual fulfilment (F1.1): confirmed → processing → packed → shipped → delivered. Each move locks the order, checks the
/// <c>rowVersion</c> (409 <c>ORDER_CHANGED</c>) and the status (409 <c>ORDER_INVALID_TRANSITION</c>), writes an audit row and an outbox event.
/// </summary>
public interface IOrderFulfilmentService
{
    Task<OrderFulfilmentDto> StartProcessingAsync(Guid orderId, OrderStepRequest request, CancellationToken ct = default);
    Task<OrderFulfilmentDto> MarkPackedAsync(Guid orderId, OrderStepRequest request, CancellationToken ct = default);
    Task<OrderFulfilmentDto> ShipAsync(Guid orderId, ShipOrderRequest request, CancellationToken ct = default);
    Task<OrderFulfilmentDto> MarkDeliveredAsync(Guid orderId, OrderStepRequest request, CancellationToken ct = default);
}

public interface IStaffOrderReadService
{
    public const int MaxPageSize = 50;

    /// <summary>The staff order list, newest first, one snapshot. A bad filter is a 400 <c>ValidationException</c>.</summary>
    Task<PagedResult<StaffOrderListItemDto>> ListAsync(StaffOrderFilter filter, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>One order in full, read in one snapshot so totals and lines agree. Unknown id: <see cref="KeyNotFoundException"/> (404).</summary>
    Task<StaffOrderDetailDto> GetAsync(Guid orderId, DateTime nowUtc, CancellationToken ct = default);
}
