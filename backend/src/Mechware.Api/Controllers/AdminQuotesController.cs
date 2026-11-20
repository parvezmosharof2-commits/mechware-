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
/// negotiated prices, so none is stored by a cache. The draft editor and its price preview are D4.7; send, approve (with an authenticator
/// code), reject approval, recall, withdraw and the approval queue are D4.8.
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

    /// <summary>
    /// Replaces a Draft's whole content (header and every line) and answers the quote with the new <c>rowVersion</c>. A stale
    /// <c>rowVersion</c> or a version that is no longer a Draft is 409; a staff member of the company is 403 <c>QUOTE_CONFLICT_OF_INTEREST</c>;
    /// a new contact must be an effective Buyer or Admin member and may change only while no version waits, is sent or is accepted.
    /// </summary>
    [HttpPut("quotes/{quoteId:guid}/versions/{versionNo:int}")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> EditDraft(Guid quoteId, int versionNo, [FromBody] EditQuoteDraftRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.EditDraftAsync(quoteId, versionNo, body, ct) });
    }

    /// <summary>
    /// What a send of this (unsaved) content would do now: totals, the buyer's own price per line, the flags and the reasons a send would be
    /// refused; cost and floor only with <c>pricing.view_cost</c>. Same body as the edit (its <c>rowVersion</c> is not needed); nothing is saved.
    /// </summary>
    [HttpPost("quotes/{quoteId:guid}/versions/{versionNo:int}/price-preview")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> PricePreview(Guid quoteId, int versionNo, [FromBody] EditQuoteDraftRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.PreviewAsync(quoteId, versionNo, body, ct) });
    }

    /// <summary>
    /// Sends a Draft (D4.8): the server checks every line now (catalogue, the buyer's own price, cost and floor) and freezes the version.
    /// No flag: <c>sent</c>, valid from now, the live version superseded. Flags (below floor, over the discount authority, long validity):
    /// <c>pending_approval</c> for a second person. A fixed delivery takes the site's address of now; a removed site is 409
    /// <c>QUOTE_DELIVERY_SITE_GONE</c>, a line below cost 409 <c>QUOTE_BELOW_COST</c>.
    /// </summary>
    [HttpPost("quotes/{quoteId:guid}/versions/{versionNo:int}/send")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> Send(Guid quoteId, int versionNo, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.SendAsync(quoteId, versionNo, ct) });
    }

    /// <summary>
    /// Approves a pending version, which sends it (valid from now). Needs <c>quotes.approve</c> (and <c>quotes.approve_below_floor</c> for a
    /// below-floor line) and a fresh authenticator code in <c>X-Step-Up-Code</c>; the approver did not create, change or submit the version;
    /// <c>contentHash</c> is the one reviewed (409 <c>QUOTE_CHANGED</c>) and both line lists equal the flags checked again now (409
    /// <c>QUOTE_FLAGS_NOT_ACKNOWLEDGED</c>, whose message names the lines).
    /// </summary>
    [HttpPost("quotes/{quoteId:guid}/versions/{versionNo:int}/approve")]
    [RequirePermission(Permissions.QuotesApprove)]
    [RequireStepUp("quote.approve")]
    public async Task<IActionResult> Approve(Guid quoteId, int versionNo, [FromBody] ApproveQuoteRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.ApproveAsync(quoteId, versionNo, body, ct) });
    }

    /// <summary>Sends a pending version back to Draft with a comment (5-500) for the author; nothing becomes binding, so no code.</summary>
    [HttpPost("quotes/{quoteId:guid}/versions/{versionNo:int}/reject-approval")]
    [RequirePermission(Permissions.QuotesApprove)]
    public async Task<IActionResult> RejectApproval(Guid quoteId, int versionNo, [FromBody] StaffCommentRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.RejectApprovalAsync(quoteId, versionNo, body, ct) });
    }

    /// <summary>Takes a pending version back to Draft before anyone approves it.</summary>
    [HttpPost("quotes/{quoteId:guid}/versions/{versionNo:int}/recall")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> Recall(Guid quoteId, int versionNo, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.RecallAsync(quoteId, versionNo, ct) });
    }

    /// <summary>Withdraws a Sent version with a reason (5-500): the buyer can no longer accept it. It cannot lower a price, so no code.</summary>
    [HttpPost("quotes/{quoteId:guid}/versions/{versionNo:int}/withdraw")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> Withdraw(Guid quoteId, int versionNo, [FromBody] StaffReasonRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await desk.WithdrawAsync(quoteId, versionNo, body, ct) });
    }

    /// <summary>The approval queue: versions waiting for a second person, oldest first, with the send's flags and whether the caller may approve.</summary>
    [HttpGet("quotes/approvals")]
    [RequirePermission(Permissions.QuotesApprove)]
    public async Task<IActionResult> Approvals([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await desk.ListApprovalsAsync(page, pageSize, ct) });
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
