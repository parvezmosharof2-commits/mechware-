using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Quotes;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff side of the quote PDF (ADR-0011 §10, §12; D4.11), <c>quotes.view</c>. The PDF of a sent version is the same stored, hash-checked file the
/// buyer gets; a draft or a version waiting for approval answers a preview ("DRAFT, NOT AN OFFER") that is never stored. Every answer is
/// <c>private, no-store</c>.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminQuotePdfController(IQuotePdfService pdfs) : ControllerBase
{
    private void Private() => Response.Headers.CacheControl = "private, no-store";

    /// <summary><c>locale</c> = <c>en</c> or <c>bn</c> (default <c>en</c>). A preview carries the header <c>X-Quote-Pdf: preview</c>.</summary>
    [HttpGet("quotes/{quoteId:guid}/versions/{versionNo:int}/pdf")]
    [RequirePermission(Permissions.QuotesView)]
    [Produces("application/pdf", "application/json")]
    public async Task<IActionResult> Pdf(Guid quoteId, int versionNo, [FromQuery] string? locale, CancellationToken ct)
    {
        Private();
        var file = await pdfs.StaffPdfAsync(quoteId, versionNo, locale, ct);
        if (file.Preview) Response.Headers["X-Quote-Pdf"] = "preview";
        return File(file.Bytes, "application/pdf", file.FileName);
    }

    /// <summary>Is this PDF ours and unmodified? The quote, version and language whose stored file has this SHA-256, or 404.</summary>
    [HttpGet("quote-documents/{sha256}")]
    [RequirePermission(Permissions.QuotesView)]
    public async Task<IActionResult> Check(string sha256, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await pdfs.CheckAsync(sha256, ct) });
    }
}
