using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Catalog;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff edit a product's product-information data (sprint 3–4): supply status and successor, alternatives, order units. Every
/// action needs <c>catalog.manage</c>, checked here in the API; changes are audited by the database interceptor.
/// </summary>
[ApiController]
[Route("api/v1/admin/products")]
public sealed class AdminProductsController(ICatalogAdminService catalog, IProductAdminService products) : ControllerBase
{
    /// <summary>Staff product list (drafts and archived included; deleted excluded). At most 50 per page.</summary>
    [HttpGet]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? status, [FromQuery] Guid? brandId, [FromQuery] Guid? categoryId,
        [FromQuery] string? supplyStatus, [FromQuery] Guid? sellerId, [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default) =>
        Ok(new { data = await products.ListAsync(q, status, brandId, categoryId, supplyStatus, sellerId, page, pageSize, ct) });

    [HttpGet("{productId:guid}")]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> Detail(Guid productId, CancellationToken ct) =>
        Ok(new { data = await products.GetAsync(productId, ct) });

    [HttpPut("{productId:guid}")]
    [RequirePermission(Permissions.CatalogManage)]
    [RequestSizeLimit(200_000)]
    public async Task<IActionResult> Update(Guid productId, [FromBody] UpdateProductRequest request, CancellationToken ct) =>
        Ok(new { data = await products.UpdateAsync(productId, request, ct) });

    [HttpPut("{productId:guid}/variants/{variantId:guid}")]
    [RequirePermission(Permissions.CatalogManage)]
    [RequestSizeLimit(10_000)]
    public async Task<IActionResult> UpdateVariant(Guid productId, Guid variantId, [FromBody] UpdateVariantRequest request, CancellationToken ct) =>
        Ok(new { data = await products.UpdateVariantAsync(productId, variantId, request, ct) });

    [HttpPost("{productId:guid}/publish")]
    [RequirePermission(Permissions.CatalogManage)]
    [RequestSizeLimit(10_000)]
    public async Task<IActionResult> Publish(Guid productId, [FromBody] ProductLifecycleRequest? request, CancellationToken ct) =>
        Ok(new { data = await products.TransitionAsync(productId, "publish", request, ct) });

    [HttpPost("{productId:guid}/archive")]
    [RequirePermission(Permissions.CatalogManage)]
    [RequestSizeLimit(10_000)]
    public async Task<IActionResult> Archive(Guid productId, [FromBody] ProductLifecycleRequest? request, CancellationToken ct) =>
        Ok(new { data = await products.TransitionAsync(productId, "archive", request, ct) });

    [HttpPost("{productId:guid}/restore")]
    [RequirePermission(Permissions.CatalogManage)]
    [RequestSizeLimit(10_000)]
    public async Task<IActionResult> Restore(Guid productId, [FromBody] ProductLifecycleRequest? request, CancellationToken ct) =>
        Ok(new { data = await products.TransitionAsync(productId, "restore", request, ct) });

    [HttpGet("{productId:guid}/pim")]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> Get(Guid productId, CancellationToken ct) =>
        Ok(new { data = await catalog.GetPimAsync(productId, ct) });

    [HttpPut("{productId:guid}/supply")]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> SetSupply(Guid productId, [FromBody] SetSupplyRequest request, CancellationToken ct) =>
        Ok(new { data = await catalog.SetSupplyAsync(productId, request, ct) });

    [HttpPut("{productId:guid}/alternatives")]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> SetAlternatives(Guid productId, [FromBody] SetAlternativesRequest request, CancellationToken ct) =>
        Ok(new { data = await catalog.SetAlternativesAsync(productId, request, ct) });

    [HttpPut("{productId:guid}/variants/{variantId:guid}/order-units")]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> SetOrderUnits(Guid productId, Guid variantId, [FromBody] SetOrderUnitsRequest request, CancellationToken ct) =>
        Ok(new { data = await catalog.SetOrderUnitsAsync(productId, variantId, request, ct) });

    [HttpPut("{productId:guid}/variants/{variantId:guid}/part-number")]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> SetPartNumber(Guid productId, Guid variantId, [FromBody] SetPartNumberRequest request, CancellationToken ct) =>
        Ok(new { data = await catalog.SetPartNumberAsync(productId, variantId, request, ct) });
}
