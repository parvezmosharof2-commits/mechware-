using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff brand and category pickers (the margin-floors screen needs ids). Allowed with <c>pricing.view_cost</c>, <c>catalog.manage</c>
/// or <c>pricelists.edit</c>. Names, slugs and ids only: no personal data, no cost.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin")]
public sealed class AdminCatalogLookupController(IAdminCatalogLookup lookup, ICurrentUserService user) : ControllerBase
{
    /// <summary>Query: <c>q</c> (part of name or slug), <c>page</c>, <c>pageSize</c> (at most 50).</summary>
    [HttpGet("brands")]
    public async Task<IActionResult> Brands([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        Require();
        return Ok(new { data = await lookup.SearchBrandsAsync(q, page, pageSize, ct) });
    }

    /// <summary>Query: <c>q</c> (part of English or Bangla name, or slug), <c>page</c>, <c>pageSize</c> (at most 50).</summary>
    [HttpGet("categories")]
    public async Task<IActionResult> Categories([FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        Require();
        return Ok(new { data = await lookup.SearchCategoriesAsync(q, page, pageSize, ct) });
    }

    private void Require()
    {
        if (!user.HasPermission(Permissions.PricingViewCost) && !user.HasPermission(Permissions.CatalogManage) && !user.HasPermission(Permissions.PriceListsEdit))
            throw new ForbiddenException("FORBIDDEN", "You do not have permission to look up brands and categories.");
    }
}
