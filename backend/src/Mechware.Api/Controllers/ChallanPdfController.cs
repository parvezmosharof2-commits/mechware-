using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Buyer side of the delivery challan (E6.4d): the order's placer, or a company Admin/Buyer of the order's company. Anything else, an unknown order
/// included, is one 404; an order that has not shipped is 409 <c>CHALLAN_NOT_AVAILABLE</c>. Rendered on demand, never stored; every answer
/// <c>private, no-store</c> with <c>Vary: Authorization</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/orders/{orderId:guid}/challan")]
public sealed class ChallanPdfController(IChallanPdfService challans, ICurrentUserService currentUser) : ControllerBase
{
    /// <summary><c>locale</c> = <c>en</c> or <c>bn</c> (default <c>en</c>).</summary>
    [HttpGet]
    [EnableRateLimiting(InvoicePdfController.ReadRateLimitPolicy)]
    [Produces("application/pdf", "application/json")]
    public async Task<IActionResult> Get(Guid orderId, [FromQuery] string? locale, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
        var actor = currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        var file = await challans.BuyerAsync(actor, orderId, locale, ct);
        return File(file.Bytes, "application/pdf", file.FileName);
    }
}

/// <summary>Staff side of the delivery challan (E6.4d): <c>orders.fulfil</c> or <c>orders.view_all</c>, either one.</summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/orders/{orderId:guid}/challan")]
public sealed class AdminChallanPdfController(IChallanPdfService challans, ICurrentUserService user) : ControllerBase
{
    [HttpGet]
    [EnableRateLimiting(InvoicePdfController.ReadRateLimitPolicy)]
    [Produces("application/pdf", "application/json")]
    public async Task<IActionResult> Get(Guid orderId, [FromQuery] string? locale, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (!user.HasPermission(Permissions.OrdersFulfil) && !user.HasPermission(Permissions.OrdersViewAll))
            throw new ForbiddenException("FORBIDDEN", "You do not have permission to print a delivery challan.");
        var file = await challans.StaffAsync(orderId, locale, ct);
        return File(file.Bytes, "application/pdf", file.FileName);
    }
}
