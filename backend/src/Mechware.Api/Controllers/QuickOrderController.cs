using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;

namespace Mechware.Api.Controllers;

/// <summary>The only thing read from the client: the pasted text. No prices, ids or company are bound.</summary>
public sealed record QuickOrderValidateBody(string? Text);

/// <summary>
/// Quick order (D5.2). Open to guests and signed-in buyers alike, with the same answer: public prices (VAT-inclusive, from the
/// price resolver without a company), the stock band the storefront shows, never cost or supplier data. Contract-price display
/// for company members is a later unit.
///
/// <c>POST /api/v1/quick-order/validate</c> with <c>{ "text": "..." }</c> answers <c>{ data: { lines, errors, notices, summary } }</c>
/// (see <see cref="QuickOrderValidation"/>). 413 <c>INPUT_TOO_LARGE</c> for a body over the limit, 422 <c>TOO_MANY_LINES</c> for more
/// than 500 content lines, 429 <c>RATE_LIMITED</c> over 30 requests a minute per IP. Never cached.
/// </summary>
[ApiController]
[NoStore]
[Route("api/v1/quick-order")]
[EnableRateLimiting(QuickOrderController.RateLimitPolicy)]
public sealed class QuickOrderController(IQuickOrderValidator validator) : ControllerBase
{
    public const string RateLimitPolicy = "QuickOrderValidate";

    /// <summary>The parser's longest input in characters, at up to 4 bytes each (UTF-8), plus the JSON envelope and escapes.</summary>
    public const int MaxBodyBytes = QuickOrderLines.MaxInputLength * 4 + 4096;

    [HttpPost("validate")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Validate([FromBody] QuickOrderValidateBody body, CancellationToken ct)
    {
        if (Request.ContentLength > MaxBodyBytes) throw new PayloadTooLargeException("INPUT_TOO_LARGE", "The request is too large.");
        return Ok(new { data = await validator.ValidateAsync(body.Text, ct) });
    }
}
