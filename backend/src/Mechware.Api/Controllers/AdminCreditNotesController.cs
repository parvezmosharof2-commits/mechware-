using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mechware.Api.Authorization;
using Mechware.Application.Invoicing;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Credit notes (Mushak-6.7; ADR-0012 §9.3, §14; E6.5b). Staff with <c>invoices.credit_request</c> (Support, Finance, Admin) ask with an
/// Idempotency-Key and may withdraw their own request; ANOTHER person with <c>invoices.credit_approve</c> (Finance, Admin) approves (issues the
/// 6.7 and its money) or rejects, both with a fresh authenticator code (step-up <c>invoice.credit_note</c>). Every answer is <c>private, no-store</c>.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminCreditNotesController(ICreditNoteService notes) : ControllerBase
{
    public const string StepUpAction = "invoice.credit_note";

    private void Private()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
    }

    [HttpPost("invoices/{invoiceId:guid}/credit-notes")]
    [RequirePermission(Permissions.InvoicesCreditRequest)]
    public async Task<IActionResult> Ask(Guid invoiceId, [FromBody] RequestCreditNoteRequest body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        Private();
        var asked = await notes.RequestAsync(invoiceId, body, idempotencyKey, ct);
        return asked.Replayed
            ? Ok(new { data = asked.Request })
            : Created($"/api/v1/admin/credit-note-requests/{asked.Request.Id}", new { data = asked.Request });
    }

    /// <summary>The queue: <c>status</c> requested (default), approved, rejected or withdrawn; oldest first; <c>pageSize</c> at most 50.</summary>
    [HttpGet("credit-note-requests")]
    [RequirePermission(Permissions.InvoicesView)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await notes.ListAsync(status, page, pageSize, ct) });
    }

    [HttpGet("credit-note-requests/{requestId:guid}")]
    [RequirePermission(Permissions.InvoicesView)]
    public async Task<IActionResult> Get(Guid requestId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await notes.GetAsync(requestId, ct) });
    }

    [HttpPost("credit-note-requests/{requestId:guid}/approve")]
    [RequirePermission(Permissions.InvoicesCreditApprove)]
    [RequireStepUp(StepUpAction)]
    public async Task<IActionResult> Approve(Guid requestId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await notes.ApproveAsync(requestId, ct) });
    }

    [HttpPost("credit-note-requests/{requestId:guid}/reject")]
    [RequirePermission(Permissions.InvoicesCreditApprove)]
    [RequireStepUp(StepUpAction)]
    public async Task<IActionResult> Reject(Guid requestId, [FromBody] RejectCreditNoteRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await notes.RejectAsync(requestId, body, ct) });
    }

    [HttpPost("credit-note-requests/{requestId:guid}/withdraw")]
    [RequirePermission(Permissions.InvoicesCreditRequest)]
    public async Task<IActionResult> Withdraw(Guid requestId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await notes.WithdrawAsync(requestId, ct) });
    }
}
