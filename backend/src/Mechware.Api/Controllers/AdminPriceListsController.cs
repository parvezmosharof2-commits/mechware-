using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Pricing;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff price lists (D2.7, D2.8; ADR-0008 §3, §11): list, read, create a draft, edit it, submit, revise, delete a draft, and
/// approve, reject, withdraw. Reading needs <c>pricelists.view</c>, editing <c>pricelists.edit</c>, the decisions
/// <c>pricelists.approve</c> (approve and withdraw also a fresh authenticator code); cost and floor values are in a response only
/// for a caller who also holds <c>pricing.view_cost</c>. The request bodies carry no status, version, approver, submitter, author
/// or series id: the actor is always the signed-in user. Changes are audited by the database interceptor, and every transition
/// also writes an explicit action row with actor and content hash.
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

    // ---- decisions (D2.8). The route parameter is named priceListId so the step-up audit row names the list it confirmed.

    /// <summary>
    /// Approve the version the approver reviewed: <c>expectedContentHash</c> from the detail and exactly its
    /// <c>belowFloorEntryIds</c>. Needs a fresh authenticator code in <c>X-Step-Up-Code</c>; the floors are checked again now.
    /// </summary>
    [HttpPost("{priceListId:guid}/approve")]
    [RequirePermission(Permissions.PriceListsApprove)]
    [RequireStepUp("pricelist.approve")]
    public async Task<IActionResult> Approve(Guid priceListId, [FromBody] ApprovePriceListRequest request, CancellationToken ct) =>
        Ok(new { data = await lists.ApproveAsync(priceListId, request, ct) });

    /// <summary>
    /// Back to Draft with a reason. No step-up: a rejection can never make a price apply or change what a buyer pays, it only
    /// sends the list back to its authors (like a refund rejection).
    /// </summary>
    [HttpPost("{priceListId:guid}/reject")]
    [RequirePermission(Permissions.PriceListsApprove)]
    public async Task<IActionResult> Reject(Guid priceListId, [FromBody] PriceListDecisionRequest request, CancellationToken ct) =>
        Ok(new { data = await lists.RejectAsync(priceListId, request, ct) });

    /// <summary>Emergency stop of an approved version, with a reason and a fresh authenticator code: it changes what buyers pay.</summary>
    [HttpPost("{priceListId:guid}/withdraw")]
    [RequirePermission(Permissions.PriceListsApprove)]
    [RequireStepUp("pricelist.withdraw")]
    public async Task<IActionResult> Withdraw(Guid priceListId, [FromBody] PriceListDecisionRequest request, CancellationToken ct) =>
        Ok(new { data = await lists.WithdrawAsync(priceListId, request, ct) });
}
