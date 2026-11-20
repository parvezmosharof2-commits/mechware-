using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Api.Controllers;

/// <summary>
/// What accepting a quote carries (ADR-0011 §8.1, QT2): the version and the content hash the buyer saw, the total the accept preview showed
/// (required), and the buyer's own choices: payment on company credit, delivery site or saved address (calculated delivery only), cost
/// centre and PO number. There are no lines, quantities, prices, coupons or company id in it: other JSON members are ignored.
/// </summary>
public sealed record AcceptQuoteBody(int? VersionNo, string? ContentHash, long? ExpectedGrandTotalMinor, bool PayOnCredit = false, Guid? ShipToSiteId = null,
    Guid? ShippingAddressId = null, Guid? CostCentreId = null, string? PurchaseOrderNumber = null);

/// <summary>The accept body without the total (§8.4): the same choices, priced as acceptance would price them now.</summary>
public sealed record AcceptPreviewBody(int? VersionNo, string? ContentHash, bool PayOnCredit = false, Guid? ShipToSiteId = null, Guid? ShippingAddressId = null,
    Guid? CostCentreId = null);

/// <summary>The buyer's "no" to a version (§8.8), with an optional "what should change" (at most 1,000 characters).</summary>
public sealed record RejectQuoteBody(int? VersionNo, string? Reason);

/// <summary>
/// The buyer's quotes (ADR-0011 §8, §12, §13; D4.9 accept, D4.10 reads, preview, reject). Only an effective member of the company in the URL,
/// which must be the quote's, gets an answer: an unknown company or quote, a non-member, platform staff, another company's quote, a draft,
/// a role that may not see it and a Pending or Rejected company all get the same 404 body; a member who sees it but may not act gets 403
/// <c>COMPANY_ROLE_REQUIRED</c>. Every answer, errors too, is <c>Cache-Control: private, no-store</c> with <c>Vary: Authorization</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/companies/{companyId:guid}/quotes")]
public sealed class CompanyQuotesController(IOrderService orders, IBuyerQuoteService quotes, IQuotePdfService pdfs, ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>30 a minute per signed-in user (QT8): by-number lookups, and the PDF (D4.11).</summary>
    public const string ReadRateLimitPolicy = "QuoteRead";

    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private void Private()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
    }

    /// <summary>The company's quotes, newest sent first: all for a role in <c>Quotes:VisibleTo</c> (Admin, Buyer), else those the caller is the contact for. <c>status</c>, <c>page</c>, <c>pageSize</c> (at most 50).</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await quotes.ListAsync(Actor, companyId, status, page, pageSize, ct) });
    }

    /// <summary>The quote with every version Sent or later (as sent), oldest first; the newest says whether it may be accepted now.</summary>
    [HttpGet("{quoteId:guid}")]
    public async Task<IActionResult> Get(Guid companyId, Guid quoteId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await quotes.GetAsync(Actor, companyId, quoteId, ct) });
    }

    /// <summary>Where an e-mailed number lives: <c>{companyId, quoteId, number}</c> for a quote the caller may see; every other number is the same 404.</summary>
    [HttpGet("/api/v1/quotes/by-number/{number}")]
    [EnableRateLimiting(ReadRateLimitPolicy)]
    public async Task<IActionResult> ByNumber(string number, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await quotes.FindByNumberAsync(Actor, number, ct) });
    }

    /// <summary>
    /// The PDF of a version the caller may see, as sent (D4.11; <c>locale</c> = <c>en</c> or <c>bn</c>, default <c>en</c>). Rendered on the first request, then served from
    /// storage after its SHA-256 is checked; anything the caller may not see is the same 404 as the reads. Rate limited like by-number.
    /// </summary>
    [HttpGet("{quoteId:guid}/versions/{versionNo:int}/pdf")]
    [EnableRateLimiting(ReadRateLimitPolicy)]
    [Produces("application/pdf", "application/json")]
    public async Task<IActionResult> Pdf(Guid companyId, Guid quoteId, int versionNo, [FromQuery] string? locale, CancellationToken ct)
    {
        Private();
        var file = await pdfs.BuyerPdfAsync(Actor, companyId, quoteId, versionNo, locale, ct);
        return File(file.Bytes, "application/pdf", file.FileName);
    }

    /// <summary>What accepting would charge now, by the acceptance's own pricing path; nothing is held or written. Send its <c>grandTotalMinor</c> as <c>expectedGrandTotalMinor</c>.</summary>
    [HttpPost("{quoteId:guid}/accept-preview")]
    public async Task<IActionResult> AcceptPreview(Guid companyId, Guid quoteId, [FromBody] AcceptPreviewBody body, CancellationToken ct)
    {
        Private();
        return Ok(new
        {
            data = await orders.PreviewQuoteAcceptanceAsync(new AcceptQuoteRequest(Actor, string.Empty, companyId, quoteId, body.VersionNo, body.ContentHash, null,
                body.PayOnCredit, body.ShipToSiteId, body.ShippingAddressId, body.CostCentreId), ct)
        });
    }

    /// <summary>
    /// Accepts the version as one order of all its lines (§8.3). <c>Idempotency-Key</c> required: 201 with the order (and <c>quoteNumber</c>),
    /// 200 for a replay with the same key and body (also after the order was cancelled), 409 <c>IDEMPOTENCY_KEY_REUSED</c> for another body.
    /// </summary>
    [HttpPost("{quoteId:guid}/accept")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Accept(Guid companyId, Guid quoteId, [FromBody] AcceptQuoteBody body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        Private();
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is required.");

        var placed = await orders.PlaceFromQuoteAsync(new AcceptQuoteRequest(Actor, idempotencyKey, companyId, quoteId, body.VersionNo, body.ContentHash,
            body.ExpectedGrandTotalMinor, body.PayOnCredit, body.ShipToSiteId, body.ShippingAddressId, body.CostCentreId, body.PurchaseOrderNumber), ct);
        return placed.Replayed ? Ok(new { data = placed }) : Created($"/api/v1/orders/{placed.OrderId}", new { data = placed });
    }

    /// <summary>Rejects a sent version (it becomes <c>rejected</c>; the quote stays open for a revision and staff are told). Answers the quote.</summary>
    [HttpPost("{quoteId:guid}/reject")]
    public async Task<IActionResult> Reject(Guid companyId, Guid quoteId, [FromBody] RejectQuoteBody body, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await quotes.RejectAsync(Actor, companyId, quoteId, body.VersionNo, body.Reason, ct) });
    }
}
