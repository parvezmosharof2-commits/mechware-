using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Mechware.Api.Authorization;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// The monthly Mushak-9.1 export (ADR-0012 §13, threat model IT12, IT17; E6.7): <c>invoices.export</c> (Finance, Admin), a per-person rate
/// limit on both formats, <c>private, no-store</c>. The JSON preview masks the BINs; the CSV carries every buyer's full BIN, so it also needs
/// an authenticator code (step-up <c>invoice.export</c>) and is audited. Never e-mailed; nothing here changes an invoice.
/// </summary>
[ApiController]
[Route("api/v1/admin/invoices/exports")]
[RequirePermission(Permissions.InvoicesExport)]
public sealed class AdminInvoiceExportsController(IVatMonthExportService exports, IIdentityService identity, ICurrentUserService currentUser) : ControllerBase
{
    public const string StepUpAction = "invoice.export";
    public const string RateLimitPolicy = "InvoiceExport";

    /// <summary><c>month</c> = a Dhaka calendar month (<c>2026-12</c>); <c>format</c> = <c>json</c> (default) or <c>csv</c> (needs <c>X-Step-Up-Code</c>).</summary>
    [HttpGet("vat-month")]
    [EnableRateLimiting(RateLimitPolicy)]
    public async Task<IActionResult> VatMonthExport([FromQuery] string? month, [FromQuery] string? format, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
        switch (format?.Trim().ToLowerInvariant())
        {
            case null or "" or "json":
                return Ok(new { data = await exports.PreviewAsync(month, ct) });
            case "csv":
                // The month is checked before the code is spent on it.
                if (!Domain.Invoicing.VatMonth.TryParse(month, out var parsed)) throw new ValidationException("month", "Use a Dhaka calendar month like 2026-12.");
                // Like [RequireStepUp], but only the CSV (full BINs) needs the code; the target names the month the code confirmed.
                await identity.VerifyStepUpAsync(currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required."),
                    Request.Headers[RequireStepUpAttribute.HeaderName].FirstOrDefault(), StepUpAction, "month=" + parsed.Label, currentUser.IpAddress, cancellationToken: ct);
                var csv = await exports.CsvAsync(month, ct);
                return File(Encoding.UTF8.GetBytes(csv.Content), "text/csv; charset=utf-8", csv.FileName);
            default:
                throw new ValidationException("format", "Use json or csv.");
        }
    }
}
