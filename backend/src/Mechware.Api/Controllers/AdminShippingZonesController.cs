using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Shipping;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff shipping zones and rate cards (E8.4a). Reading, the preview and the coverage report need <c>shipping.view</c>;
/// create, replace and delete need <c>shipping.manage</c> (Admin). Request bodies carry no id, author or timestamp: the server
/// owns them. Until a zone exists, production refuses every order (E8.2), so the coverage report says what is still uncovered.
/// </summary>
[ApiController]
[Route("api/v1/admin/shipping")]
public sealed class AdminShippingZonesController(IShippingZoneService zones) : ControllerBase
{
    [HttpGet("zones")]
    [RequirePermission(Permissions.ShippingView)]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new { data = await zones.ListAsync(ct) });

    [HttpGet("zones/{id:guid}")]
    [RequirePermission(Permissions.ShippingView)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(new { data = await zones.GetAsync(id, ct) });

    [HttpPost("zones")]
    [RequirePermission(Permissions.ShippingManage)]
    public async Task<IActionResult> Create([FromBody] ShippingZoneRequest request, CancellationToken ct)
    {
        var created = await zones.CreateAsync(request, ct);
        return Created($"/api/v1/admin/shipping/zones/{created.Id}", new { data = created });
    }

    [HttpPut("zones/{id:guid}")]
    [RequirePermission(Permissions.ShippingManage)]
    public async Task<IActionResult> Update(Guid id, [FromBody] ShippingZoneRequest request, CancellationToken ct) =>
        Ok(new { data = await zones.UpdateAsync(id, request, ct) });

    [HttpDelete("zones/{id:guid}")]
    [RequirePermission(Permissions.ShippingManage)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await zones.DeleteAsync(id, ct);
        return NoContent();
    }

    /// <summary>Dry run of the calculator against the current active zones. Writes nothing.</summary>
    [HttpPost("zones/preview")]
    [RequirePermission(Permissions.ShippingView)]
    public async Task<IActionResult> Preview([FromBody] ShippingPreviewRequest request, CancellationToken ct) =>
        Ok(new { data = await zones.PreviewAsync(request, ct) });

    [HttpGet("coverage")]
    [RequirePermission(Permissions.ShippingView)]
    public async Task<IActionResult> Coverage(CancellationToken ct) => Ok(new { data = await zones.CoverageAsync(ct) });
}
