using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff variant lookup for screens that pick a variant by SKU (the price-list editor, E8.3b). Allowed with
/// <c>pricelists.edit</c>, <c>catalog.manage</c> or <c>quotes.edit</c> (the quote editor, D4.18; the answer also carries the seller, minimum order and pack size). Drafts and unpublished products are included, since only staff reach it. The cost
/// price is in a response only for a caller who also holds <c>pricing.view_cost</c>. No personal data.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/admin/variants")]
public sealed class AdminVariantsController(IAdminVariantLookup lookup, ICurrentUserService user) : ControllerBase
{
    /// <summary>Query: <c>sku</c> (exact, case-insensitive), <c>q</c> (part of SKU, part number or title), <c>page</c>, <c>pageSize</c> (at most 50).</summary>
    [HttpGet]
    public async Task<IActionResult> Search([FromQuery] string? sku, [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        if (!user.HasPermission(Permissions.PriceListsEdit) && !user.HasPermission(Permissions.CatalogManage)
            && !user.HasPermission(Permissions.QuotesEdit))
            throw new ForbiddenException("FORBIDDEN", "You do not have permission to look up variants.");
        return Ok(new { data = await lookup.SearchAsync(sku, q, page, pageSize, user.HasPermission(Permissions.PricingViewCost), ct) });
    }
}
