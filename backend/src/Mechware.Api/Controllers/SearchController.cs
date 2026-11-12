using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Mechware.Application.Search;

namespace Mechware.Api.Controllers;

/// <summary>Public product search (C6.3). Bad input is a 400, an engine failure a 503; both are mapped by the global middleware.</summary>
[ApiController]
[Route("api/v1/search")]
[EnableRateLimiting(StorefrontController.ReadRateLimitPolicy)]
public sealed class SearchController(IStorefrontSearch search) : ControllerBase
{
    /// <summary>Searches products by free text or part number, with brand, category, stock, price and numeric-attribute (<c>attr=code:min..max</c>) filters. A value with a unit in the text ("750 W") matches by measure.</summary>
    [HttpGet]
    public async Task<IActionResult> Search(
        [FromQuery] string? q, [FromQuery] int page = 1, [FromQuery] int pageSize = 24,
        [FromQuery] string[]? brand = null, [FromQuery] string? category = null, [FromQuery] bool inStock = false,
        [FromQuery] long? minPriceMinor = null, [FromQuery] long? maxPriceMinor = null, [FromQuery] string? sort = null, [FromQuery] string[]? attr = null,
        CancellationToken ct = default)
    {
        var result = await search.SearchAsync(new StorefrontSearchRequest
        {
            Text = q,
            Page = page,
            PageSize = pageSize,
            Brands = brand ?? [],
            Category = category,
            InStockOnly = inStock,
            MinPriceMinor = minPriceMinor,
            MaxPriceMinor = maxPriceMinor,
            Sort = sort,
            Attributes = attr ?? [],
        }, ct);

        Response.Headers.CacheControl = "public, max-age=30";
        return Ok(new
        {
            data = result.Hits,
            meta = new { result.Page, result.PageSize, result.Total, result.MatchedBy, result.ProcessingMs, result.Facets },
        });
    }

    /// <summary>Title suggestions for a prefix, at most 10.</summary>
    [HttpGet("autocomplete")]
    public async Task<IActionResult> Autocomplete([FromQuery] string? q, [FromQuery] int limit = 5, CancellationToken ct = default)
    {
        var result = await search.AutocompleteAsync(q, limit, ct);
        Response.Headers.CacheControl = "public, max-age=60";
        return Ok(new { data = result.Suggestions, meta = new { result.ProcessingMs } });
    }
}
