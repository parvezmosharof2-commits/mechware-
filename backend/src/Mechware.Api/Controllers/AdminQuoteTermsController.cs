using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Quotes;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// The default terms lines printed on quote PDFs (D4.20; owner decision 2026-11-20). Reading needs <c>quotes.view</c>; changing them needs
/// <c>quotes.approve</c> (Finance and Admin), because the lines are commercial promises printed on every customer quotation. A save needs the
/// <c>rowVersion</c> of the last read (409 <c>QUOTE_TERMS_CHANGED</c> when stale) and writes the audit row <c>quote.terms_changed</c>. A quote
/// that was already sent keeps the lines it was sent with. Every answer is <c>private, no-store</c>.
/// </summary>
[ApiController]
[Route("api/v1/admin/quote-terms")]
public sealed class AdminQuoteTermsController(IQuoteTermsService terms) : ControllerBase
{
    private void Private() => Response.Headers.CacheControl = "private, no-store";

    [HttpGet]
    [RequirePermission(Permissions.QuotesView)]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        Private();
        return Ok(new { data = await terms.GetAsync(ct) });
    }

    [HttpPut]
    [RequirePermission(Permissions.QuotesApprove)]
    public async Task<IActionResult> Put([FromBody] UpdateQuoteTermsRequest request, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await terms.UpdateAsync(request, ct) });
    }
}
