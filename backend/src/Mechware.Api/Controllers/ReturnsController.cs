using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// What the buyer asks to return: order lines and quantities, a reason (<c>damaged</c>, <c>wrong_item</c>, <c>not_as_described</c>,
/// <c>other</c>) and an optional note (plain text, at most 1000; required for <c>other</c>). <c>CompanyId</c> names the company of a
/// company order and is empty for a personal one; it is checked against the order, never stored from here.
/// </summary>
public sealed record OpenReturnBody(IReadOnlyList<ReturnLineInput>? Lines, string? ReasonCode, string? Note, Guid? CompanyId = null);

/// <summary>
/// F2.1 buyer returns. Only the caller's own orders and returns; anything else, however malformed, is the same 404. Every answer is
/// <c>private, no-store</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class ReturnsController(IReturnRequestService returns, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    /// <summary>
    /// Opens a return of a delivered order within the return window (409 <c>RETURN_ORDER_NOT_DELIVERED</c>, <c>RETURN_WINDOW_CLOSED</c>,
    /// <c>RETURN_QUANTITY_EXCEEDED</c>; 422 <c>RETURN_LINE_NOT_RETURNABLE</c>). The <c>Idempotency-Key</c> header is required: the same key and
    /// body answer 200 with the first return, another body is 409 <c>IDEMPOTENCY_KEY_REUSED</c>.
    /// </summary>
    [HttpPost("orders/{orderNumber}/returns")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Open(string orderNumber, [FromBody] OpenReturnBody body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        Private();
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is required.");
        var opened = await returns.OpenAsync(new OpenReturnRequest(Actor, orderNumber, idempotencyKey, body?.CompanyId, body?.ReasonCode, body?.Note, body?.Lines), ct);
        return opened.Replayed ? Ok(new { data = opened.Return }) : Created($"/api/v1/returns/{opened.Return.Id}", new { data = opened.Return });
    }

    /// <summary>The caller's own returns, newest first. <c>pageSize</c> at most 50.</summary>
    [HttpGet("returns")]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await returns.ListOwnAsync(Actor, page, pageSize, ct) });
    }

    [HttpGet("returns/{returnId:guid}")]
    public async Task<IActionResult> Get(Guid returnId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await returns.GetOwnAsync(Actor, returnId, ct) });
    }

    /// <summary>Withdraws the caller's own open return; again answers it unchanged; once staff handle it, 409 <c>RETURN_NOT_WITHDRAWABLE</c>.</summary>
    [HttpPost("returns/{returnId:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid returnId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await returns.WithdrawAsync(Actor, returnId, ct) });
    }

    private void Private() => Response.Headers.CacheControl = "private, no-store";
}

/// <summary>F2.1 staff reads of every return (<c>orders.view_all</c>: Support, Finance, Admin). Decisions come with F2.3. <c>private, no-store</c>.</summary>
[ApiController]
[Route("api/v1/admin/returns")]
public sealed class AdminReturnsController(IReturnRequestService returns) : ControllerBase
{
    /// <summary>Newest first. Filters: <c>status</c> (wire name), <c>orderNumber</c> (prefix), <c>companyId</c>. <c>pageSize</c> at most 50; a bad filter is 400.</summary>
    [HttpGet]
    [RequirePermission(Permissions.OrdersViewAll)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? orderNumber, [FromQuery] Guid? companyId,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = await returns.ListStaffAsync(new StaffReturnFilter(status, orderNumber, companyId, page, pageSize), ct) });
    }

    [HttpGet("{returnId:guid}")]
    [RequirePermission(Permissions.OrdersViewAll)]
    public async Task<IActionResult> Get(Guid returnId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = await returns.GetStaffAsync(returnId, ct) });
    }
}
