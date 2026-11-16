using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Orders;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>MECHWARE staff views over all orders.</summary>
[ApiController]
[Route("api/v1/admin/orders")]
public sealed class AdminOrdersController(IOrderDeadlineService deadlines, IOrderCancellationService cancellations, TimeProvider clock) : ControllerBase
{
    public const string CancelStepUpAction = "order.cancel";

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
}
