using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Mechware.Api.Authorization;
using Mechware.Application.Credit;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff reads of the credit ledger (ADR-0010 §9-§10, threat model CS27; B3.8). Everything needs <c>credit.view</c> (Finance, Admin;
/// Support gets 403). Read-only: nothing here writes the ledger or locks a row. These are the URLs the <c>Location</c> headers of the
/// payment and adjustment requests point at. A CSV is audited and rate-limited; the all-company aging CSV also needs an authenticator code.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
[RequirePermission(Permissions.CreditView)]
public sealed class AdminCreditReadsController(ICreditReadService reads) : ControllerBase
{
    public const string ExportAllStepUpAction = "credit.export_all";
    public const string ExportRateLimitPolicy = "CreditExport";

    /// <summary>The verification queue: <c>status</c> (recorded by default, verified, rejected, bounced, all), <c>companyId</c>, <c>page</c>, <c>pageSize</c> (at most 200).</summary>
    [HttpGet("credit/payments")]
    public async Task<IActionResult> Payments([FromQuery] string? status, [FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(new { data = await reads.ListPaymentsAsync(status, companyId, page, pageSize, ct) });

    [HttpGet("credit/payments/{paymentId:guid}")]
    public async Task<IActionResult> Payment(Guid paymentId, CancellationToken ct) => Ok(new { data = await reads.GetPaymentAsync(paymentId, ct) });

    /// <summary>The adjustments queue: <c>status</c> (requested by default, approved, rejected, all), <c>companyId</c>, <c>page</c>, <c>pageSize</c>.</summary>
    [HttpGet("credit/adjustments")]
    public async Task<IActionResult> Adjustments([FromQuery] string? status, [FromQuery] Guid? companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(new { data = await reads.ListAdjustmentsAsync(status, companyId, page, pageSize, ct) });

    [HttpGet("credit/adjustments/{adjustmentId:guid}")]
    public async Task<IActionResult> Adjustment(Guid adjustmentId, CancellationToken ct) => Ok(new { data = await reads.GetAdjustmentAsync(adjustmentId, ct) });

    /// <summary>Aging: current, 1-30, 31-60, 61-90, 90+ days past due. Query: <c>companyId</c>, <c>q</c> (part of the name), <c>overdueOnly</c>, <c>page</c>, <c>pageSize</c>.</summary>
    [HttpGet("credit/aging")]
    public async Task<IActionResult> Aging([FromQuery] Guid? companyId, [FromQuery] string? q, [FromQuery] bool overdueOnly = false, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(new { data = await reads.AgingAsync(companyId, q, overdueOnly, page, pageSize, ct) });

    /// <summary>Every company's books in one file: authenticator code (<c>credit.export_all</c>), audited, rate-limited.</summary>
    [HttpGet("credit/aging.csv")]
    [RequireStepUp(ExportAllStepUpAction)]
    [EnableRateLimiting(ExportRateLimitPolicy)]
    public async Task<IActionResult> AgingCsv([FromQuery] bool overdueOnly = false, CancellationToken ct = default) => Csv(await reads.AgingCsvAsync(overdueOnly, ct));

    /// <summary>Ledger lines of one company, in posting order. Query: <c>from</c>, <c>to</c> (Dhaka days, inclusive), <c>page</c>, <c>pageSize</c>.</summary>
    [HttpGet("companies/{companyId:guid}/credit/statement")]
    public async Task<IActionResult> Statement(Guid companyId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 50, CancellationToken ct = default) =>
        Ok(new { data = await reads.StatementAsync(companyId, from, to, page, pageSize, ct) });

    [HttpGet("companies/{companyId:guid}/credit/statement.csv")]
    [EnableRateLimiting(ExportRateLimitPolicy)]
    public async Task<IActionResult> StatementCsv(Guid companyId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        Csv(await reads.StatementCsvAsync(companyId, from, to, ct));

    private FileContentResult Csv(CreditCsvExport export)
    {
        Response.Headers.CacheControl = "private, no-store";
        return File(Encoding.UTF8.GetBytes(export.Content), "text/csv; charset=utf-8", export.FileName);
    }
}
