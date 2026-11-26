using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Mechware.Api.Authorization;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Buyer side of the invoice PDF (ADR-0012 §12, §14; E6.4b). Access is exactly the buyer's order detail; an order or invoice the caller may not see, an invoice of
/// another order and an unknown id all get the same 404. The file key is never read from the request. Every answer, errors too, is
/// <c>private, no-store</c> with <c>Vary: Authorization</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/orders/{orderId:guid}/invoices")]
public sealed class InvoicePdfController(IInvoicePdfService pdfs, ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>30 a minute per signed-in user, for buyer and staff PDF downloads.</summary>
    public const string ReadRateLimitPolicy = "InvoiceRead";

    /// <summary><c>locale</c> = <c>en</c> or <c>bn</c> (default <c>en</c>).</summary>
    [HttpGet("{invoiceId:guid}/pdf")]
    [EnableRateLimiting(ReadRateLimitPolicy)]
    [Produces("application/pdf", "application/json")]
    public async Task<IActionResult> Pdf(Guid orderId, Guid invoiceId, [FromQuery] string? locale, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
        var actor = currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        var file = await pdfs.BuyerPdfAsync(actor, orderId, invoiceId, locale, ct);
        return File(file.Bytes, "application/pdf", file.FileName);
    }
}

/// <summary>Staff side of the invoice PDF (ADR-0012 §12, §14; E6.4b), <c>invoices.view</c>: the same stored, hash-checked file, and the document check by SHA-256.</summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminInvoicePdfController(IInvoicePdfService pdfs) : ControllerBase
{
    private void Private() => Response.Headers.CacheControl = "private, no-store";

    [HttpGet("invoices/{invoiceId:guid}/pdf")]
    [RequirePermission(Permissions.InvoicesView)]
    [EnableRateLimiting(InvoicePdfController.ReadRateLimitPolicy)]
    [Produces("application/pdf", "application/json")]
    public async Task<IActionResult> Pdf(Guid invoiceId, [FromQuery] string? locale, CancellationToken ct)
    {
        Private();
        var file = await pdfs.StaffPdfAsync(invoiceId, locale, ct);
        return File(file.Bytes, "application/pdf", file.FileName);
    }

    /// <summary>Is this PDF ours and unmodified? The invoice and language whose stored file has this SHA-256, or 404.</summary>
    [HttpGet("invoice-documents/{sha256}")]
    [RequirePermission(Permissions.InvoicesView)]
    public async Task<IActionResult> Check(string sha256, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await pdfs.CheckAsync(sha256, ct) });
    }
}
