using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Pricing;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff price lists, part 1 (D2.7, ADR-0008 §11): list, read, create a draft, edit it, submit, revise, delete a draft.
/// Approve, reject and withdraw are D2.8. Reading needs <c>pricelists.view</c>, everything else <c>pricelists.edit</c>; cost
/// and floor values are in a response only for a caller who also holds <c>pricing.view_cost</c>. The request bodies carry no
/// status, version, approver, submitter, author or series id: whoever sends them is ignored. Changes are audited by the
/// database interceptor, and submit, revise and delete also write an explicit action row.
/// </summary>
[ApiController]
[Route("api/v1/admin/price-lists")]
public sealed class AdminPriceListsController(IPriceListService lists) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.PriceListsView)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] string? scope, [FromQuery] Guid? companyId,
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(new { data = await lists.ListAsync(new PriceListQuery(status, scope, companyId, q, page, pageSize), ct) });

    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.PriceListsView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(new { data = await lists.GetAsync(id, ct) });

    [HttpPost]
    [RequirePermission(Permissions.PriceListsEdit)]
    public async Task<IActionResult> Create([FromBody] CreatePriceListRequest request, CancellationToken ct)
    {
        var created = await lists.CreateAsync(request, ct);
        return Created($"/api/v1/admin/price-lists/{created.Id}", new { data = created });
    }

    [HttpPut("{id:guid}")]
    [RequirePermission(Permissions.PriceListsEdit)]
    public async Task<IActionResult> UpdateHeader(Guid id, [FromBody] UpdatePriceListHeaderRequest request, CancellationToken ct) =>
        Ok(new { data = await lists.UpdateHeaderAsync(id, request, ct) });

    [HttpPut("{id:guid}/entries")]
    [RequirePermission(Permissions.PriceListsEdit)]
    public async Task<IActionResult> UpsertEntries(Guid id, [FromBody] UpsertPriceListEntriesRequest request, CancellationToken ct) =>
        Ok(new { data = await lists.UpsertEntriesAsync(id, request, ct) });

    [HttpDelete("{id:guid}/entries/{entryId:guid}")]
    [RequirePermission(Permissions.PriceListsEdit)]
    public async Task<IActionResult> RemoveEntry(Guid id, Guid entryId, CancellationToken ct) =>
        Ok(new { data = await lists.RemoveEntryAsync(id, entryId, ct) });

    [HttpPost("{id:guid}/submit")]
    [RequirePermission(Permissions.PriceListsEdit)]
    public async Task<IActionResult> Submit(Guid id, CancellationToken ct) => Ok(new { data = await lists.SubmitAsync(id, ct) });

    [HttpPost("{id:guid}/revise")]
    [RequirePermission(Permissions.PriceListsEdit)]
    public async Task<IActionResult> Revise(Guid id, CancellationToken ct)
    {
        var revised = await lists.ReviseAsync(id, ct);
        return Created($"/api/v1/admin/price-lists/{revised.Id}", new { data = revised });
    }

    [HttpDelete("{id:guid}")]
    [RequirePermission(Permissions.PriceListsEdit)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await lists.DeleteDraftAsync(id, ct);
        return NoContent();
    }
}
