using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Pricing;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff margin floors (D2.9; ADR-0008 §4, threat model PL11). Reading needs <c>pricing.view_cost</c> (the floors describe
/// the cost structure); changing one needs <c>pricing.manage_floors</c> and a fresh authenticator code. Responses carry
/// markups only, never a cost. Every change writes an explicit audit row with the old and the new markup. A floor change
/// never approves a price list: a pending list is evaluated again with the floors of the moment it is approved.
/// </summary>
[ApiController]
[Route("api/v1/admin/margin-floors")]
public sealed class AdminMarginFloorsController(IMarginFloorService floors) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.PricingViewCost)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new { data = await floors.ListAsync(ct) });

    /// <summary>The row that applies to a variant, or to a brand and/or category, by the policy's precedence, and its markup.</summary>
    [HttpGet("effective")]
    [RequirePermission(Permissions.PricingViewCost)]
    public async Task<IActionResult> Effective([FromQuery] Guid? variantId, [FromQuery] Guid? brandId, [FromQuery] Guid? categoryId, CancellationToken ct) =>
        Ok(new { data = await floors.EffectiveAsync(variantId, brandId, categoryId, ct) });

    /// <summary>Create the row for (scope, brandId, categoryId) or change its markup (0–1,000 %, at most two decimals).</summary>
    [HttpPut]
    [RequirePermission(Permissions.PricingManageFloors)]
    [RequireStepUp("margin-floor.change")]
    public async Task<IActionResult> Upsert([FromBody] UpsertMarginFloorRequest request, CancellationToken ct) =>
        Ok(new { data = await floors.UpsertAsync(request, ct) });

    /// <summary>Delete a brand or category row; the global row cannot be deleted (409 <c>GLOBAL_FLOOR_REQUIRED</c>).</summary>
    [HttpDelete("{marginFloorId:guid}")]
    [RequirePermission(Permissions.PricingManageFloors)]
    [RequireStepUp("margin-floor.delete")]
    public async Task<IActionResult> Delete(Guid marginFloorId, CancellationToken ct)
    {
        await floors.DeleteAsync(marginFloorId, ct);
        return NoContent();
    }
}
