using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Catalog;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff pick a seller by store name (the console catalogue import). Needs <c>catalog.manage</c>, like the import itself. The list
/// carries id, store name and status only: no owner, e-mail, phone or payout data.
/// </summary>
[ApiController]
[Route("api/v1/admin/sellers")]
public sealed class AdminSellersController(ICatalogAdminService catalog) : ControllerBase
{
    /// <summary>Query: <c>status</c> (pending, approved by default, rejected, suspended), <c>q</c> (part of the store name), <c>page</c>, <c>pageSize</c> (at most 50).</summary>
    [HttpGet]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(new { data = await catalog.ListSellersAsync(status, q, page, pageSize, ct) });
}
