using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mechware.Api.Authorization;
using Mechware.Application.Invoicing;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// VDS/AIT certificates (ADR-0012 §11, §14; E6.6b, threat model IT14), the credit-payment maker-checker: Finance records with
/// <c>credit.record_payment</c> and an Idempotency-Key; ANOTHER Finance/Admin verifies with <c>credit.verify_payment</c> and a fresh code
/// (step-up <c>invoice.withholding</c>; the ledger line follows in the same transaction) or rejects with a reason. Staff with
/// <c>invoices.view</c> read them. Every answer is <c>private, no-store</c>.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminWithholdingsController(ITaxWithholdingService withholdings) : ControllerBase
{
    public const string VerifyStepUpAction = "invoice.withholding";
    public const string EntityStepUpAction = "company.withholding_entity";

    private void Private()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
    }

    [HttpPost("invoices/{invoiceId:guid}/withholdings")]
    [RequirePermission(Permissions.CreditRecordPayment)]
    public async Task<IActionResult> Record(Guid invoiceId, [FromBody] RecordWithholdingRequest body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey,
        CancellationToken ct)
    {
        Private();
        var recorded = await withholdings.RecordAsync(invoiceId, body, idempotencyKey, ct);
        return recorded.Replayed
            ? Ok(new { data = recorded.Withholding })
            : Created($"/api/v1/admin/withholdings/{recorded.Withholding.Id}", new { data = recorded.Withholding });
    }

    /// <summary><c>status</c> recorded (default), verified or rejected; <c>kind</c> vds or ait; oldest first; <c>pageSize</c> at most 50.</summary>
    [HttpGet("withholdings")]
    [RequirePermission(Permissions.InvoicesView)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? kind, [FromQuery] Guid? invoiceId, [FromQuery] int page = 1,
        [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await withholdings.ListAsync(status, kind, invoiceId, page, pageSize, ct) });
    }

    [HttpGet("withholdings/{withholdingId:guid}")]
    [RequirePermission(Permissions.InvoicesView)]
    public async Task<IActionResult> Get(Guid withholdingId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await withholdings.GetAsync(withholdingId, ct) });
    }

    [HttpPost("withholdings/{withholdingId:guid}/verify")]
    [RequirePermission(Permissions.CreditVerifyPayment)]
    [RequireStepUp(VerifyStepUpAction)]
    public async Task<IActionResult> Verify(Guid withholdingId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await withholdings.VerifyAsync(withholdingId, ct) });
    }

    /// <summary>Four eyes too, no code: nothing moves; the certificate number is free again for a corrected entry.</summary>
    [HttpPost("withholdings/{withholdingId:guid}/reject")]
    [RequirePermission(Permissions.CreditVerifyPayment)]
    public async Task<IActionResult> Reject(Guid withholdingId, [FromBody] RejectWithholdingRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await withholdings.RejectAsync(withholdingId, body, ct) });
    }

    /// <summary>The company deducts VAT/tax at source: only a hint on the invoice PDF and payment text, never an amount (Admin, step-up, audited).</summary>
    [HttpPut("companies/{companyId:guid}/withholding-entity")]
    [RequirePermission(Permissions.CompaniesManage)]
    [RequireStepUp(EntityStepUpAction)]
    public async Task<IActionResult> SetWithholdingEntity(Guid companyId, [FromBody] SetWithholdingEntityRequest body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await withholdings.SetWithholdingEntityAsync(companyId, body, ct) });
    }
}
