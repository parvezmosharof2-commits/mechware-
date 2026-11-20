using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Quotes;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Small staff lookups for the quote editor (D4.18): a company's delivery sites, the people who may be a quote's contact, and the sellers
/// of given variants (one shipping fee per seller). All need <c>quotes.edit</c>; answers are field whitelists (no phone numbers, no cost,
/// no secrets) and never cached. An unknown company is 404.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminQuoteLookupsController(IStaffQuoteLookups lookups) : ControllerBase
{
    private void Private() => Response.Headers.CacheControl = "private, no-store";

    /// <summary>The company's delivery sites, the default one first.</summary>
    [HttpGet("companies/{companyId:guid}/sites")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> Sites(Guid companyId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await lookups.SitesAsync(companyId, ct) });
    }

    /// <summary>Active, accepted members of the company with the Buyer or Admin role: who a quote may be addressed to.</summary>
    [HttpGet("companies/{companyId:guid}/quote-contacts")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> Contacts(Guid companyId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await lookups.ContactsAsync(companyId, ct) });
    }

    /// <summary><c>?variantIds=a&amp;variantIds=b</c> (1 to 50): the seller of each variant.</summary>
    [HttpGet("quote-sellers")]
    [RequirePermission(Permissions.QuotesEdit)]
    public async Task<IActionResult> Sellers([FromQuery] Guid[] variantIds, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await lookups.SellersOfAsync(variantIds, ct) });
    }
}
