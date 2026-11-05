using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using ShopVerse.Application.Identity;
using ShopVerse.Infrastructure.Identity;

namespace ShopVerse.Api.Authentication;

/// <summary>
/// Validates short-lived (15m) Bearer JWT access tokens from the Authorization header.
/// </summary>
public sealed class JwtBearerAuthenticationHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "Bearer";

    private readonly ITokenService _tokenService;

    public JwtBearerAuthenticationHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder,
        ITokenService tokenService)
        : base(options, logger, encoder)
    {
        _tokenService = tokenService;
    }

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue("Authorization", out var authHeaderValues))
        {
            return AuthenticateResult.NoResult();
        }

        string authHeader = authHeaderValues.ToString();
        if (!authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return AuthenticateResult.NoResult();
        }

        string token = authHeader["Bearer ".Length..].Trim();
        if (string.IsNullOrEmpty(token))
        {
            return AuthenticateResult.Fail("Bearer token is empty.");
        }

        ClaimsPrincipal? principal = _tokenService.ValidateAccessToken(token, DateTime.UtcNow);
        if (principal is null || !Guid.TryParse(principal.FindFirstValue("sub"), out Guid userId))
        {
            return AuthenticateResult.Fail("Invalid or expired JWT access token.");
        }

        // M4: the signature proves who minted the token, not that the account, the session and the roles are still as they
        // were. Those come from the database on every request.
        // A token that names no session cannot be ended by signing out, so it is not accepted.
        if (!Guid.TryParseExact(principal.FindFirstValue(JwtTokenService.SessionIdClaimType), "N", out Guid sessionId))
        {
            return AuthenticateResult.Fail("The access token names no session.");
        }

        ISessionValidator validator = Context.RequestServices.GetRequiredService<ISessionValidator>();
        LiveSession? live = await validator.ValidateAsync(userId, sessionId, DateTime.UtcNow, Context.RequestAborted);
        if (live is null)
        {
            return AuthenticateResult.Fail("The session has ended or the account is no longer active.");
        }

        var claims = principal.Claims
            .Where(c => c.Type != ClaimTypes.Role
                && !string.Equals(c.Type, JwtTokenService.PermissionClaimType, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(c.Type, JwtTokenService.SellerIdClaimType, StringComparison.OrdinalIgnoreCase))
            .ToList();
        claims.AddRange(live.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(live.Permissions.Select(p => new Claim(JwtTokenService.PermissionClaimType, p)));
        if (live.SellerId is { } sellerId)
        {
            claims.Add(new Claim(JwtTokenService.SellerIdClaimType, sellerId.ToString()));
        }
        var current = new ClaimsPrincipal(new ClaimsIdentity(claims, principal.Identity?.AuthenticationType ?? SchemeName));

        return AuthenticateResult.Success(new AuthenticationTicket(current, SchemeName));
    }
}
