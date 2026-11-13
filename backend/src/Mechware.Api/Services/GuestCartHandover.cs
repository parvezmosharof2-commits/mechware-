using Mechware.Api.Controllers;
using Mechware.Application.Cart;

namespace Mechware.Api.Services;

/// <summary>
/// E1.4: when a session starts (password, OTP, Google, 2FA enrolment, refresh), the guest cart named by this browser's
/// <c>__Host-mw_cart</c> cookie is merged into the account's cart and the cookie is expired, so a replayed guest token
/// afterwards finds nothing (CT10). On logout the cookie is expired too, so the next guest cart gets a new id.
///
/// A merge problem never fails the sign-in: it is logged, the cookie is kept and the guest cart stays as it was, so the next
/// sign-in (or refresh) tries again. A forged, expired or garbled cookie is ignored silently and expired.
/// </summary>
public sealed class GuestCartHandover(ICartMergeService merges, IGuestCartTokenService tokens, ILogger<GuestCartHandover> logger)
{
    /// <summary>Null when there was nothing to merge or the merge did not happen.</summary>
    public async Task<CartMergeResult?> OnSignedInAsync(HttpContext http, Guid userId)
    {
        string? raw = http.Request.Cookies[CartController.GuestCookieName];
        if (raw is null) return null;
        if (tokens.Validate(raw) is not { } guestId)
        {
            Expire(http.Response);
            return null;
        }

        try
        {
            var result = await merges.MergeAsync(userId, guestId, CancellationToken.None);
            Expire(http.Response);
            return result;
        }
        catch (Exception ex)   // deliberate: no cart problem may turn a successful sign-in into an error
        {
            logger.LogWarning(ex, "The guest cart {GuestCart} was not merged into the account at sign-in; it is kept for the next try.",
                guestId.Value.ToString("N")[..8]);
            return null;
        }
    }

    /// <summary>Expires the guest-cart cookie with the attributes it was set with (a <c>__Host-</c> cookie needs Secure, Path=/).</summary>
    public static void Expire(HttpResponse response) =>
        response.Cookies.Delete(CartController.GuestCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/",
            IsEssential = true
        });
}
