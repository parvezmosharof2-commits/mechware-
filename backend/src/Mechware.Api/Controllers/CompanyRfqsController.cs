using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Net.Http.Headers;
using Mechware.Application.Identity;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Api.Controllers;

/// <summary>
/// What an RFQ request carries. <c>source</c> is <c>cart</c>, <c>text</c>, <c>csv</c> or <c>product</c>; only that source's own member is read
/// (<c>text</c>, or <c>variantId</c> and <c>quantity</c>). There is no line list, price or company in it: other JSON members are ignored.
/// </summary>
public sealed record CreateRfqBody(string? Source, string? Text, Guid? VariantId, int? Quantity, string? Message, DateOnly? NeededBy, Guid? ShipToSiteId);

/// <summary>
/// The buyer's requests for quote (ADR-0011 §2, §12, §13; D4.5, threat model QT1, QT10). Only an effective member of the company in the
/// URL gets an answer; an unknown company, a non-member, platform staff without membership, another company's or another member's RFQ
/// and a Pending or Rejected company all get the same 404 body. Every answer, errors too, is <c>Cache-Control: private, no-store</c> with
/// <c>Vary: Authorization</c>. <c>POST</c> needs an <c>Idempotency-Key</c> (201, or 200 for a replay).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/companies/{companyId:guid}/rfqs")]
public sealed class CompanyRfqsController(IBuyerRfqService rfqs, ICurrentUserService currentUser) : ControllerBase
{
    public const string RateLimitPolicy = "QuoteRequests";

    /// <summary>The longest pasted text (100,000 characters) at up to 4 bytes each, plus the JSON around it and the other members.</summary>
    public const int MaxBodyBytes = QuickOrderController.MaxBodyBytes + 16_384;

    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private void Private()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
    }

    [HttpPost]
    [EnableRateLimiting(RateLimitPolicy)]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Create(Guid companyId, [FromBody] CreateRfqBody body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        Private();
        if (Request.ContentLength > MaxBodyBytes) throw new PayloadTooLargeException("INPUT_TOO_LARGE", "The request is too large.");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is required.");

        var created = await rfqs.CreateAsync(new CreateRfqRequest(Actor, companyId, idempotencyKey, body.Source, body.Text, body.VariantId, body.Quantity, body.Message, body.NeededBy, body.ShipToSiteId), ct);
        return created.Replayed
            ? Ok(new { data = created.Rfq })
            : Created($"/api/v1/companies/{companyId}/rfqs/{created.Rfq.Rfq.Id}", new { data = created.Rfq });
    }

    /// <summary>The company's RFQs, newest first: all of them for an Admin or Buyer, otherwise the caller's own. <c>status</c>, <c>page</c>, <c>pageSize</c> (at most 50).</summary>
    [HttpGet]
    public async Task<IActionResult> List(Guid companyId, [FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await rfqs.ListAsync(Actor, companyId, status, page, pageSize, ct) });
    }

    [HttpGet("{rfqId:guid}")]
    public async Task<IActionResult> Get(Guid companyId, Guid rfqId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await rfqs.GetAsync(Actor, companyId, rfqId, ct) });
    }

    /// <summary>Withdraws an open RFQ (any time before staff close it). Withdrawing twice is one withdrawal.</summary>
    [HttpPost("{rfqId:guid}/withdraw")]
    public async Task<IActionResult> Withdraw(Guid companyId, Guid rfqId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await rfqs.WithdrawAsync(Actor, companyId, rfqId, ct) });
    }
}
