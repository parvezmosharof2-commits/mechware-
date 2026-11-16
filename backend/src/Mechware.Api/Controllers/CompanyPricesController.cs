using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mechware.Application.Identity;
using Mechware.Application.Pricing;

namespace Mechware.Api.Controllers;

/// <summary>
/// A signed-in buyer's company prices (D2.10, ADR-0008 §11; threat model §৮ PL6, PL9, PL12, PL17).
/// <c>GET /api/v1/companies/{companyId}/prices?items=variantId[:qty],…</c> (at most 100, quantity 1 … 10,000, default 1) answers
/// <c>{ data: { companyId, asOfUtc, items: [CompanyPriceDto] } }</c> in the order asked. Only a member with the Buyer role of an
/// active company gets it; for anyone else the company is 404 with one body. Every answer (also an error) is
/// <c>Cache-Control: private, no-store</c> and <c>Vary: Authorization</c>, so no shared cache, CDN or browser keeps it. Reads only;
/// the per-user global rate limit applies.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/companies/{companyId:guid}/prices")]
public sealed class CompanyPricesController(ICompanyPriceService prices, ICurrentUserService currentUser) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid companyId, [FromQuery] string? items, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
        var actor = currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        return Ok(new { data = await prices.GetAsync(actor, companyId, items, ct) });
    }
}
