using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Api.Controllers;

/// <summary>
/// What accepting a quote carries (ADR-0011 §8.1, QT2): the version and the content hash the buyer saw, the total the accept preview showed
/// (required), and the buyer's own choices: payment on company credit, delivery site or saved address (calculated delivery only), cost
/// centre and PO number. There are no lines, quantities, prices, coupons or company id in it: other JSON members are ignored.
/// </summary>
public sealed record AcceptQuoteBody(int? VersionNo, string? ContentHash, long? ExpectedGrandTotalMinor, bool PayOnCredit = false, Guid? ShipToSiteId = null,
    Guid? ShippingAddressId = null, Guid? CostCentreId = null, string? PurchaseOrderNumber = null);

/// <summary>
/// The buyer's quotes (ADR-0011 §8, §12, §13; D4.9 accept, the reads follow in D4.10). Only an effective member of the company in the URL,
/// which must be the quote's, gets an answer: an unknown company or quote, a non-member, platform staff, another company's quote, a draft
/// and a Pending or Rejected company all get the same 404 body; a member without the Buyer role gets 403 <c>COMPANY_ROLE_REQUIRED</c>.
/// Every answer, errors too, is <c>Cache-Control: private, no-store</c> with <c>Vary: Authorization</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/companies/{companyId:guid}/quotes")]
public sealed class CompanyQuotesController(IOrderService orders, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    /// <summary>
    /// Accepts the version as one order of all its lines (§8.3). <c>Idempotency-Key</c> required: 201 with the order (and <c>quoteNumber</c>),
    /// 200 for a replay with the same key and body (also after the order was cancelled), 409 <c>IDEMPOTENCY_KEY_REUSED</c> for another body.
    /// </summary>
    [HttpPost("{quoteId:guid}/accept")]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Accept(Guid companyId, Guid quoteId, [FromBody] AcceptQuoteBody body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
        if (string.IsNullOrWhiteSpace(idempotencyKey)) throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is required.");

        var placed = await orders.PlaceFromQuoteAsync(new AcceptQuoteRequest(Actor, idempotencyKey, companyId, quoteId, body.VersionNo, body.ContentHash,
            body.ExpectedGrandTotalMinor, body.PayOnCredit, body.ShipToSiteId, body.ShippingAddressId, body.CostCentreId, body.PurchaseOrderNumber), ct);
        return placed.Replayed ? Ok(new { data = placed }) : Created($"/api/v1/orders/{placed.OrderId}", new { data = placed });
    }
}
