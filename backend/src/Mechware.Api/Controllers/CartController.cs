using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Mechware.Application.Cart;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Api.Controllers;

/// <summary>
/// What the buyer asks for. Nothing else is bound: a price, unit price or total in the JSON is ignored, because every price
/// on the response is the server's own. <c>Quantity</c> is required (no default).
/// </summary>
public sealed record AddCartItemBody(Guid VariantId, int? Quantity);

public sealed record SetCartItemBody(int? Quantity);

/// <summary>Cart responses hold one buyer's data and must never be stored by a browser or a shared cache.</summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NoStoreAttribute : ActionFilterAttribute
{
    public override void OnActionExecuting(ActionExecutingContext context) =>
        context.HttpContext.Response.Headers.CacheControl = "no-store";
}

/// <summary>
/// The shopping cart (E1.3). Open to guests: a signed-in user's cart is keyed by the user id of the access token, a guest's by
/// the signed id in the <c>__Host-mw_cart</c> cookie. Neither is ever read from the URL, a header or the body.
///
/// Contract: every response is <c>{ data: CartView }</c> and carries the stored cart's <c>ETag</c> (header and <c>data.etag</c>).
/// A change is applied on top of the latest stored cart (the server re-reads and retries on a race, 3 attempts, then 409
/// <c>CART_BUSY</c>), so a client does not need to send the ETag; one that wants "only if unchanged" sends <c>If-Match</c>
/// (409 <c>CART_CHANGED</c> otherwise). Quantity 0 on PUT removes the line; POST requires a positive quantity.
/// </summary>
[ApiController]
[NoStore]
[Route("api/v1/cart")]
public sealed class CartController(
    ICartService cart, IGuestCartTokenService tokens, ICurrentUserService currentUser,
    IGuestCartCreationLimiter limiter, ICorsPolicyProvider corsPolicies) : ControllerBase
{
    /// <summary>The <c>__Host-</c> prefix makes a browser accept the cookie only with Secure, Path=/ and no Domain.</summary>
    public const string GuestCookieName = "__Host-mw_cart";
    private const int MaxBodyBytes = 4096;

    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var (owner, guestId) = Identify();
        return owner is null ? Respond(CartView.Empty, null) : Respond(await cart.GetAsync(owner.Value, ct), guestId);
    }

    [HttpPost("items")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Add([FromBody] AddCartItemBody body, CancellationToken ct)
    {
        RefuseLargeBody();
        if (body.VariantId == Guid.Empty) throw new ValidationException("variantId", "A variant id is required.");
        int quantity = Positive(body.Quantity);
        return await WriteAsync(creates: true, new CartOperation.Add(body.VariantId, quantity), ct);
    }

    [HttpPut("items/{variantId:guid}")]
    [RequestSizeLimit(MaxBodyBytes)]
    public async Task<IActionResult> Set(Guid variantId, [FromBody] SetCartItemBody body, CancellationToken ct)
    {
        RefuseLargeBody();
        if (body.Quantity is null or < 0) throw new ValidationException("quantity", "Quantity must be a whole number, zero or more.");
        return await WriteAsync(creates: body.Quantity > 0, new CartOperation.Set(variantId, body.Quantity.Value), ct);
    }

    [HttpDelete("items/{variantId:guid}")]
    public Task<IActionResult> Remove(Guid variantId, CancellationToken ct) =>
        WriteAsync(creates: false, new CartOperation.Remove(variantId), ct);

    [HttpDelete]
    public async Task<IActionResult> Clear(CancellationToken ct)
    {
        await EnforceOriginForGuestAsync();
        var (owner, _) = Identify();
        if (owner is null) return Respond(CartView.Empty, null);
        return Respond(await cart.ClearAsync(owner.Value, ct), null);
    }

    private async Task<IActionResult> WriteAsync(bool creates, CartOperation operation, CancellationToken ct)
    {
        await EnforceOriginForGuestAsync();
        var (owner, guestId) = Identify();
        if (owner is null)
        {
            // No valid guest cart. Only a write that can create one mints an id (CT11), and only within the per-IP limit.
            if (!creates) return Respond(CartView.Empty, null);
            if (!limiter.TryAcquire(currentUser.IpAddress)) throw new TooManyRequestsException();
            guestId = GuestCartId.New();
            owner = CartOwner.FromValidatedGuestToken(guestId.Value);
        }

        var view = await cart.ApplyAsync(owner.Value, operation, IfMatch(), ct);
        return Respond(view, guestId);
    }

    /// <summary>
    /// The signed-in user (from the access token, never from input), else the guest of a valid cookie token, else nobody.
    /// A forged, expired or garbled token is "no cart", silently: the answer never says why.
    /// </summary>
    private (CartOwner? Owner, GuestCartId? GuestId) Identify()
    {
        if (currentUser.UserId is { } userId) return (CartOwner.ForUser(userId), null);
        if (tokens.Validate(Request.Cookies[GuestCookieName]) is { } guestId) return (CartOwner.FromValidatedGuestToken(guestId), guestId);
        return (null, null);
    }

    private IActionResult Respond(CartView view, GuestCartId? guestId)
    {
        // Sliding: a guest whose cart exists gets a fresh token (new expiry) with every response.
        if (guestId is { } id && view.ETag is not null)
        {
            Response.Cookies.Append(GuestCookieName, tokens.Issue(id), new CookieOptions
            {
                HttpOnly = true,
                Secure = true,
                SameSite = SameSiteMode.Lax,
                Path = "/",
                MaxAge = ICartStore.GuestTtl,
                IsEssential = true
            });
        }

        if (view.ETag is not null) Response.Headers.ETag = $"\"{view.ETag}\"";
        return Ok(new { data = view });
    }

    private string? IfMatch()
    {
        string? raw = Request.Headers.IfMatch.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return raw.Trim().Trim('"');
    }

    /// <summary>[RequestSizeLimit] makes Kestrel stop reading at the limit (also for chunked bodies); this answers 413 for a declared length.</summary>
    private void RefuseLargeBody()
    {
        if (Request.ContentLength > MaxBodyBytes) throw new PayloadTooLargeException("CART_BODY_TOO_LARGE", "The request is too large.");
    }

    private static int Positive(int? quantity) =>
        quantity is > 0 ? quantity.Value : throw new ValidationException("quantity", "Quantity must be a whole number greater than zero.");

    /// <summary>
    /// A guest cart rides on a cookie the browser attaches by itself, so a browser's Origin must be one of ours (the CORS
    /// allow-list, or this API's own origin). Requests without an Origin (scripts, server calls) carry no ambient cookie risk.
    /// SameSite=Lax already keeps the cookie off cross-site POST/PUT/DELETE; this is the second layer for same-site siblings.
    /// </summary>
    private async Task EnforceOriginForGuestAsync()
    {
        if (currentUser.UserId is not null) return;   // a signed-in user proves themselves with the Authorization header, not a cookie
        string? origin = Request.Headers.Origin.ToString();
        if (string.IsNullOrEmpty(origin)) return;
        if (string.Equals(origin, $"{Request.Scheme}://{Request.Host}", StringComparison.OrdinalIgnoreCase)) return;
        var policy = await corsPolicies.GetPolicyAsync(HttpContext, "MechWareCors");
        if (policy is null || !policy.Origins.Contains(origin, StringComparer.OrdinalIgnoreCase))
            throw new ForbiddenException("ORIGIN_NOT_ALLOWED", "This request is not allowed from this site.");
    }
}
