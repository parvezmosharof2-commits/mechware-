using Microsoft.AspNetCore.Mvc;
using ShopVerse.Api.Authorization;
using ShopVerse.Application.Catalog;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Api.Controllers;

/// <summary>
/// Staff edit a product's product-information data (sprint 3–4): supply status and successor, alternatives, order units. Every
/// action needs <c>catalog.manage</c>, checked here in the API; changes are audited by the database interceptor.
/// </summary>
[ApiController]
[Route("api/v1/admin/products")]
public sealed class AdminProductsController(ICatalogAdminService catalog) : ControllerBase
{
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
