using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShopVerse.Application.Catalog;

namespace ShopVerse.Api.Controllers;

/// <summary>Public, cache-backed storefront read endpoints (Phase 4 read models).</summary>
[ApiController]
[Route("api/v1/storefront")]
[EnableRateLimiting(StorefrontController.ReadRateLimitPolicy)]
public sealed class StorefrontController : ControllerBase
{
    public const string ReadRateLimitPolicy = "StorefrontRead";
    private const string PublicCache = "public, max-age=30, stale-while-revalidate=60";

    private readonly IStorefrontCatalogReader _catalog;

    public StorefrontController(IStorefrontCatalogReader catalog) => _catalog = catalog;

    /// <summary>Product detail page payload. Retired slugs answer 301 to the current slug.</summary>
    [HttpGet("products/{slug}")]
    [ProducesResponseType(typeof(ProductDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status301MovedPermanently)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetProductBySlug(string slug, CancellationToken ct)
    {
        var result = await _catalog.GetProductBySlugAsync(slug, ct);
        if (result.IsRedirect)
        {
            Response.Headers.CacheControl = "public, max-age=3600";
            return RedirectPermanent($"/api/v1/storefront/products/{result.RedirectToSlug}");
        }

        if (!result.IsFound)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Product not found",
                detail: $"No active product with slug '{slug}'.", type: "https://mechware.com.bd/errors/product-not-found");
        }

        Response.Headers.CacheControl = PublicCache;
        return Ok(new { data = result.Value });
    }

    [HttpGet("categories")]
    public async Task<IActionResult> GetCategoryTree(CancellationToken ct)
    {
        Response.Headers.CacheControl = "public, max-age=300";
        return Ok(new { data = await _catalog.GetCategoryTreeAsync(ct) });
    }

    /// <summary>Products in a category and all of its descendants, best sellers first.</summary>
    [HttpGet("categories/{slug}/products")]
    public async Task<IActionResult> ListCategoryProducts(
        string slug, [FromQuery] int page = 1, [FromQuery] int pageSize = 24, CancellationToken ct = default)
    {
        var result = await _catalog.ListProductsByCategoryAsync(slug, page, pageSize, ct);
        if (result.IsRedirect)
        {
            return RedirectPermanent($"/api/v1/storefront/categories/{result.RedirectToSlug}/products?page={page}&pageSize={pageSize}");
        }

        if (!result.IsFound)
        {
            return Problem(statusCode: StatusCodes.Status404NotFound, title: "Category not found",
                detail: $"No category with slug '{slug}'.", type: "https://mechware.com.bd/errors/category-not-found");
        }

        Response.Headers.CacheControl = "public, max-age=30";
        return Ok(new
        {
            data = result.Value!.Items,
            meta = new { page = result.Value.Page, pageSize = result.Value.PageSize, totalCount = result.Value.TotalCount }
        });
    }
}
