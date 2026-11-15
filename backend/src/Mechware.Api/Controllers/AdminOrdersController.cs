using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Orders;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>MECHWARE staff views over all orders.</summary>
[ApiController]
[Route("api/v1/admin/orders")]
public sealed class AdminOrdersController(IOrderDeadlineService deadlines, TimeProvider clock) : ControllerBase
{
    /// <summary>
    /// Orders past a legal deadline right now (E8.3): past the delivery deadline and not delivered, or past the courier handover
    /// deadline and not shipped. Order number, status, district and deadlines only. <c>pageSize</c> is at most 50.
    /// </summary>
    [HttpGet("overdue")]
    [RequirePermission(Permissions.OrdersViewAll)]
    public async Task<IActionResult> Overdue([FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(new { data = await deadlines.ListOverdueAsync(clock.GetUtcNow().UtcDateTime, page, pageSize, ct) });
}
