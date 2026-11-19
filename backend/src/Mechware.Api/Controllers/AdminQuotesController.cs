using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Quotes;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// The staff quote desk (ADR-0011 §12, §13; D4.6, threat model QT1, QT6): the RFQ inbox with its SLA clock, assignment and closing of
/// RFQs, and quotes around their drafts: create (from an RFQ with its matched lines, or for a company), list, read every version,
/// revise and close. Reads need <c>quotes.view</c>, commands <c>quotes.edit</c>; cost figures need <c>pricing.view_cost</c> as well.
/// A staff member of the buyer's company may not create or revise its quote (403 <c>QUOTE_CONFLICT_OF_INTEREST</c>). Answers carry
/// negotiated prices, so none is stored by a cache. Editing, sending and approving are D4.7 and D4.8.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminQuotesController(IStaffQuoteService desk) : ControllerBase
{
    private void Private() => Response.Headers.CacheControl = "private, no-store";

    /// <summary>
    /// The RFQ inbox. <c>status</c>: <c>open</c> (Submitted or Quoting, oldest first) or one status; <c>assigned</c>: <c>me</c>, <c>none</c>
    /// or a staff user id; <c>companyId</c>; <c>q</c> (number or company name); <c>page</c>, <c>pageSize</c> (at most 50).
    /// </summary>
    [HttpGet("rfqs")]
    [RequirePermission(Permissions.QuotesView)]
    public async Task<IActionResult> ListRfqs([FromQuery] string? status, [FromQuery] string? assigned, [FromQuery] Guid? companyId, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await desk.ListRfqsAsync(new StaffQuoteQuery(status, assigned, companyId, q, page, pageSize), ct) });
    }

    [HttpGet("rfqs/{rfqId:guid}")]
    [RequirePermission(Permissions.QuotesView)]
    public async Task<IActionResult> GetRfq(Guid rfqId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.GetRfqAsync(rfqId, ct) });
    }

    /// <summary>Assigns an open RFQ to a staff member who prepares quotes, or unassigns it (<c>assigneeId: null</c>).</summary>
    [HttpPost("rfqs/{rfqId:guid}/assign")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> AssignRfq(Guid rfqId, [FromBody] AssignRfqRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.AssignRfqAsync(rfqId, body, ct) });
    }

    /// <summary>Closes an RFQ with a reason the buyer sees (for example "no bid"); its quotes stay as they are.</summary>
    [HttpPost("rfqs/{rfqId:guid}/close")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> CloseRfq(Guid rfqId, [FromBody] StaffReasonRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.CloseRfqAsync(rfqId, body, ct) });
    }

    /// <summary><c>{rfqId}</c> (the matched lines pre-filled at the company's own price) or <c>{companyId, contactUserId}</c>; version 1 is a Draft.</summary>
    [HttpPost("quotes")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> CreateQuote([FromBody] CreateStaffQuoteRequest body, CancellationToken ct)
    {
        Private();
        var created = await desk.CreateQuoteAsync(body, ct);
        return Created($"/api/v1/admin/quotes/{created.Quote.Id}", new { data = created });
    }

    /// <summary>
    /// Quotes, newest first. <c>status</c>: <c>draft</c>, <c>pending_approval</c>, <c>sent</c>, <c>expired</c>, <c>accepted</c>, <c>rejected</c>,
    /// <c>withdrawn</c> (of the latest version) or <c>closed</c>; <c>assigned</c>, <c>companyId</c>, <c>q</c>, <c>page</c>, <c>pageSize</c>.
    /// </summary>
    [HttpGet("quotes")]
    [RequirePermission(Permissions.QuotesView)]
    public async Task<IActionResult> ListQuotes([FromQuery] string? status, [FromQuery] string? assigned, [FromQuery] Guid? companyId, [FromQuery] string? q,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await desk.ListQuotesAsync(new StaffQuoteQuery(status, assigned, companyId, q, page, pageSize), ct) });
    }

    [HttpGet("quotes/{quoteId:guid}")]
    [RequirePermission(Permissions.QuotesView)]
    public async Task<IActionResult> GetQuote(Guid quoteId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.GetQuoteAsync(quoteId, ct) });
    }

    /// <summary>Copies the latest version into a new Draft while no version is open and no order of the quote is live.</summary>
    [HttpPost("quotes/{quoteId:guid}/revise")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> ReviseQuote(Guid quoteId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.ReviseQuoteAsync(quoteId, ct) });
    }

    /// <summary>Ends the quote with a reason: its Sent version is withdrawn and a pending one recalled, in the same transaction.</summary>
    [HttpPost("quotes/{quoteId:guid}/close")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> CloseQuote(Guid quoteId, [FromBody] StaffReasonRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.CloseQuoteAsync(quoteId, body, ct) });
    }
}
