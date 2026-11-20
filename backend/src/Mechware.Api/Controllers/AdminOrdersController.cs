using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Orders;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>MECHWARE staff views over all orders.</summary>
[ApiController]
[Route("api/v1/admin/orders")]
public sealed class AdminOrdersController(
    IOrderDeadlineService deadlines, IOrderCancellationService cancellations, IStaffOrderReadService reads, IOrderFulfilmentService fulfilment,
    TimeProvider clock) : ControllerBase
{
    public const string CancelStepUpAction = "order.cancel";

    // F1.1 manual fulfilment (orders.fulfil: Support, Admin). Body carries the order's rowVersion (stale: 409 ORDER_CHANGED); a move
    // from the wrong status is 409 ORDER_INVALID_TRANSITION. Each writes an audit row and an outbox event.

    /// <summary>Confirmed → processing.</summary>
    [HttpPost("{orderId:guid}/processing")]
    [RequirePermission(Permissions.OrdersFulfil)]
    public async Task<IActionResult> Processing(Guid orderId, [FromBody] OrderStepRequest body, CancellationToken ct) =>
        Ok(new { data = await fulfilment.StartProcessingAsync(orderId, body, ct) });

    /// <summary>Processing → packed.</summary>
    [HttpPost("{orderId:guid}/packed")]
    [RequirePermission(Permissions.OrdersFulfil)]
    public async Task<IActionResult> Packed(Guid orderId, [FromBody] OrderStepRequest body, CancellationToken ct) =>
        Ok(new { data = await fulfilment.MarkPackedAsync(orderId, body, ct) });

    /// <summary>Packed → shipped: courier name (2-80), tracking number (1-60 letters, digits, dashes), optional https tracking link on a known courier site.</summary>
    [HttpPost("{orderId:guid}/ship")]
    [RequirePermission(Permissions.OrdersFulfil)]
    public async Task<IActionResult> Ship(Guid orderId, [FromBody] ShipOrderRequest body, CancellationToken ct) =>
        Ok(new { data = await fulfilment.ShipAsync(orderId, body, ct) });

    /// <summary>Shipped (or out for delivery) → delivered.</summary>
    [HttpPost("{orderId:guid}/delivered")]
    [RequirePermission(Permissions.OrdersFulfil)]
    public async Task<IActionResult> Delivered(Guid orderId, [FromBody] OrderStepRequest body, CancellationToken ct) =>
        Ok(new { data = await fulfilment.MarkDeliveredAsync(orderId, body, ct) });

    /// <summary>
    /// Cancels a confirmed, processing or packed order (E7.2): stock back to the warehouses it came from, a credit order's charge
    /// reduced (paid money goes on account), captured gateway payments into the refund queue. Always an authenticator code and a
    /// reason (5-500). Calling it again for a cancelled order answers 200 with <c>alreadyCancelled</c> and changes nothing else.
    /// </summary>
    [HttpPost("{orderId:guid}/cancel")]
    [RequirePermission(Permissions.OrdersCancel)]
    [RequireStepUp(CancelStepUpAction)]
    public async Task<IActionResult> Cancel(Guid orderId, [FromBody] CancelConfirmedOrderRequest body, CancellationToken ct) =>
        Ok(new { data = await cancellations.CancelConfirmedAsync(orderId, body, ct) });

    /// <summary>
    /// Orders past a legal deadline right now (E8.3): past the delivery deadline and not delivered, or past the courier handover
    /// deadline and not shipped. Order number, status, district and deadlines only. <c>pageSize</c> is at most 50.
    /// </summary>
    [HttpGet("overdue")]
    [RequirePermission(Permissions.OrdersViewAll)]
    public async Task<IActionResult> Overdue([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(new { data = await deadlines.ListOverdueAsync(clock.GetUtcNow().UtcDateTime, page, pageSize, ct) });

    /// <summary>
    /// Staff order list (G5.5), newest first. Filters: <c>status</c> (wire name), <c>companyId</c>, <c>orderNumber</c> (prefix), <c>from</c>/<c>to</c>
    /// (Dhaka dates, inclusive), <c>paymentState</c> (none, on_credit, pending, captured, failed, needs_refund, refunded), <c>overdue</c>.
    /// <c>pageSize</c> at most 50. A bad filter is 400. No buyer name, phone or address. <c>private, no-store</c>.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.OrdersViewAll)]
    public async Task<IActionResult> List(
        [FromQuery] string? status, [FromQuery] Guid? companyId, [FromQuery] string? orderNumber, [FromQuery] string? from, [FromQuery] string? to,
        [FromQuery] string? paymentState, [FromQuery] bool? overdue, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var result = await reads.ListAsync(new StaffOrderFilter(status, companyId, orderNumber, from, to, paymentState, overdue, page, pageSize),
            clock.GetUtcNow().UtcDateTime, ct);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = result });
    }

    /// <summary>
    /// One order in full for staff (G5.5): lines with the price snapshot, totals, shipping, delivery address, buyer name and phone (personal
    /// data, never logged), status timeline, payments with refunds (no provider references or card data), credit standing, quote link.
    /// 404 for an unknown id. <c>private, no-store</c>.
    /// </summary>
    [HttpGet("{orderId:guid}")]
    [RequirePermission(Permissions.OrdersViewAll)]
    public async Task<IActionResult> Get(Guid orderId, CancellationToken ct)
    {
        var result = await reads.GetAsync(orderId, clock.GetUtcNow().UtcDateTime, ct);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = result });
    }
}
