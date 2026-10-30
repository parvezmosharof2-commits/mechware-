using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using ShopVerse.Api.Authorization;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Api.Controllers;

[ApiController]
[Route("api/v1/auth")]
public sealed class AuthController : ControllerBase
{
    public const string RefreshTokenCookieName = "refresh_token";
    public const string AuthStrictRateLimitPolicy = "AuthStrictPolicy";

    private readonly IIdentityService _identityService;
    private readonly ICurrentUserService _currentUser;

    public AuthController(IIdentityService identityService, ICurrentUserService currentUser)
    {
        _identityService = identityService;
        _currentUser = currentUser;
    }

    [HttpPost("register")]
    [EnableRateLimiting(AuthStrictRateLimitPolicy)]
    public async Task<IActionResult> Register(
        [FromBody] RegisterRequestDto request,
        CancellationToken cancellationToken)
    {
        (RegisterResponseDto response, _) = await _identityService.RegisterAsync(
            request,
            _currentUser.IpAddress,
            cancellationToken);

        return StatusCode(StatusCodes.Status201Created, new
        {
            data = new
            {
                userId = response.UserId,
                status = response.Status,
                verificationChannel = response.VerificationChannel,
                referenceId = response.ReferenceId,
                expiresInSeconds = response.ExpiresInSeconds
            }
        });
    }

    [HttpPost("otp/send")]
    [EnableRateLimiting(AuthStrictRateLimitPolicy)]
    public async Task<IActionResult> SendOtp(
        [FromBody] SendOtpRequestDto request,
        CancellationToken cancellationToken)
    {
        (SendOtpResponseDto response, _) = await _identityService.SendOtpAsync(
            request,
            _currentUser.IpAddress,
            cancellationToken);

        return Ok(new
        {
            data = new
            {
                recipient = response.Recipient,
                type = response.Type,
                referenceId = response.ReferenceId,
                expiresInSeconds = response.ExpiresInSeconds
            }
        });
    }

    [HttpPost("verify-otp")]
    [EnableRateLimiting(AuthStrictRateLimitPolicy)]
    public async Task<IActionResult> VerifyOtp([FromBody] VerifyOtpRequestDto request)
    {
        AuthTokenResponseDto result = await _identityService.VerifyOtpAsync(
            request,
            _currentUser.IpAddress,
            _currentUser.UserAgent);

        WriteRefreshCookieIfPresent(result.RefreshTokenCookieValue);
        return Ok(new { data = MapTokenEnvelope(result) });
    }

    [HttpPost("login")]
    [EnableRateLimiting(AuthStrictRateLimitPolicy)]
    public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
    {
        AuthTokenResponseDto result = await _identityService.LoginAsync(
            request,
            _currentUser.IpAddress,
            _currentUser.UserAgent);

        if (result.RequiresTwoFactor)
        {
            return StatusCode(StatusCodes.Status202Accepted, new
            {
                data = new
                {
                    requiresTwoFactor = true,
                    twoFactorChallengeToken = result.TwoFactorChallengeToken,
                    user = result.User
                }
            });
        }

        WriteRefreshCookieIfPresent(result.RefreshTokenCookieValue);
        return Ok(new { data = MapTokenEnvelope(result) });
    }

    [HttpPost("google")]
    [EnableRateLimiting(AuthStrictRateLimitPolicy)]
    public async Task<IActionResult> GoogleLogin([FromBody] GoogleLoginRequestDto request)
    {
        AuthTokenResponseDto result = await _identityService.GoogleLoginAsync(
            request,
            _currentUser.IpAddress,
            _currentUser.UserAgent);

        WriteRefreshCookieIfPresent(result.RefreshTokenCookieValue);
        return Ok(new { data = MapTokenEnvelope(result) });
    }

    [HttpPost("refresh")]
    public async Task<IActionResult> Refresh()
    {
        string? rawRefreshToken = Request.Cookies[RefreshTokenCookieName];
        AuthTokenResponseDto result = await _identityService.RotateRefreshTokenAsync(
            rawRefreshToken ?? string.Empty,
            _currentUser.IpAddress,
            _currentUser.UserAgent);

        WriteRefreshCookieIfPresent(result.RefreshTokenCookieValue);

        return Ok(new
        {
            data = new
            {
                accessToken = result.AccessToken,
                expiresIn = result.ExpiresIn,
                tokenType = result.TokenType
            }
        });
    }

    [HttpPost("logout")]
    public async Task<IActionResult> Logout()
    {
        string? rawRefreshToken = Request.Cookies[RefreshTokenCookieName];
        bool revokedByCookie = await _identityService.RevokeRefreshTokenByCookieAsync(rawRefreshToken, _currentUser.IpAddress);

        if (!revokedByCookie && _currentUser.UserId.HasValue)
        {
            await _identityService.RevokeSessionsAsync(
                _currentUser.UserId.Value,
                new RevokeSessionsRequestDto(RevokeAll: false),
                _currentUser.IpAddress,
                rawRefreshToken);
        }

        Response.Cookies.Delete(RefreshTokenCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/api/v1/auth"
        });

        return Ok(new { data = new { loggedOut = true } });
    }

    [HttpPost("password/forgot")]
    [EnableRateLimiting(AuthStrictRateLimitPolicy)]
    public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto request)
    {
        (string referenceId, _) = await _identityService.InitiatePasswordResetAsync(
            request.Identifier,
            _currentUser.IpAddress);

        return Ok(new
        {
            data = new
            {
                referenceId,
                expiresInSeconds = 300,
                message = "If an account matches that identifier, a password reset code has been dispatched."
            }
        });
    }

    [HttpPost("password/reset")]
    [EnableRateLimiting(AuthStrictRateLimitPolicy)]
    public async Task<IActionResult> ResetPassword([FromBody] ResetPasswordRequestDto request)
    {
        await _identityService.CompletePasswordResetAsync(request, _currentUser.IpAddress);
        return Ok(new { data = new { passwordReset = true } });
    }

    [Authorize]
    [HttpPost("2fa/setup")]
    public async Task<IActionResult> SetupTwoFactor()
    {
        Guid userId = RequireAuthenticatedUserId();
        TotpSetupResponseDto result = await _identityService.SetupTwoFactorAsync(userId, _currentUser.IpAddress);

        return Ok(new
        {
            data = new
            {
                secret = result.Secret,
                otpAuthUri = result.OtpAuthUri,
                recoveryCodes = result.RecoveryCodes
            }
        });
    }

    [Authorize]
    [HttpPost("2fa/verify")]
    public async Task<IActionResult> VerifyTwoFactor([FromBody] TotpVerifyRequestDto request)
    {
        Guid userId = RequireAuthenticatedUserId();
        await _identityService.VerifyAndEnableTwoFactorAsync(userId, request.Code, _currentUser.IpAddress);
        return Ok(new { data = new { twoFactorEnabled = true } });
    }

    [Authorize]
    [HttpGet("sessions")]
    public async Task<IActionResult> GetSessions()
    {
        Guid userId = RequireAuthenticatedUserId();
        string? rawRefreshToken = Request.Cookies[RefreshTokenCookieName];
        IReadOnlyList<ActiveSessionDto> sessions = await _identityService.GetActiveSessionsAsync(userId, rawRefreshToken);
        return Ok(new { data = sessions });
    }

    [Authorize]
    [HttpPost("sessions/revoke")]
    public async Task<IActionResult> RevokeSessions([FromBody] RevokeSessionsRequestDto request)
    {
        Guid userId = RequireAuthenticatedUserId();
        string? rawRefreshToken = Request.Cookies[RefreshTokenCookieName];
        int revokedCount = await _identityService.RevokeSessionsAsync(userId, request, _currentUser.IpAddress, rawRefreshToken);
        return Ok(new { data = new { revokedCount } });
    }

    private Guid RequireAuthenticatedUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("User authentication is required.");

    private void WriteRefreshCookieIfPresent(string? rawRefreshToken)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return;
        }

        Response.Cookies.Append(RefreshTokenCookieName, rawRefreshToken, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/api/v1/auth",
            MaxAge = TimeSpan.FromDays(7)
        });
    }

    private static object MapTokenEnvelope(AuthTokenResponseDto result) =>
        new
        {
            accessToken = result.AccessToken,
            expiresIn = result.ExpiresIn,
            tokenType = result.TokenType,
            user = result.User
        };
}

[ApiController]
[Authorize]
[Route("api/v1/users/me")]
public sealed class UsersController : ControllerBase
{
    private readonly IIdentityService _identityService;
    private readonly ICurrentUserService _currentUser;

    public UsersController(IIdentityService identityService, ICurrentUserService currentUser)
    {
        _identityService = identityService;
        _currentUser = currentUser;
    }

    [HttpGet]
    public async Task<IActionResult> GetMe()
    {
        Guid userId = RequireAuthenticatedUserId();
        return Ok(new { data = await _identityService.GetUserProfileAsync(userId) });
    }

    [HttpGet("addresses")]
    public async Task<IActionResult> GetAddresses()
    {
        Guid userId = RequireAuthenticatedUserId();
        return Ok(new { data = await _identityService.GetAddressesAsync(userId) });
    }

    [HttpPost("addresses")]
    public async Task<IActionResult> CreateAddress([FromBody] UpsertAddressRequestDto request)
    {
        Guid userId = RequireAuthenticatedUserId();
        AddressDto created = await _identityService.CreateAddressAsync(userId, request, _currentUser.IpAddress);
        return StatusCode(StatusCodes.Status201Created, new { data = created });
    }

    [HttpPut("addresses/{addressId:guid}")]
    public async Task<IActionResult> UpdateAddress(Guid addressId, [FromBody] UpsertAddressRequestDto request)
    {
        Guid userId = RequireAuthenticatedUserId();
        AddressDto updated = await _identityService.UpdateAddressAsync(userId, addressId, request, _currentUser.IpAddress);
        return Ok(new { data = updated });
    }

    [HttpDelete("addresses/{addressId:guid}")]
    public async Task<IActionResult> DeleteAddress(Guid addressId)
    {
        Guid userId = RequireAuthenticatedUserId();
        await _identityService.DeleteAddressAsync(userId, addressId, _currentUser.IpAddress);
        return NoContent();
    }

    [HttpGet("export")]
    public async Task<IActionResult> ExportGdprBundle()
    {
        Guid userId = RequireAuthenticatedUserId();
        GdprExportBundleDto bundle = await _identityService.ExportUserDataAsync(userId, _currentUser.IpAddress);
        return Ok(new { data = bundle });
    }

    [HttpDelete]
    public async Task<IActionResult> AnonymizeAccount()
    {
        Guid userId = RequireAuthenticatedUserId();
        await _identityService.AnonymizeAndEraseUserAsync(userId, _currentUser.IpAddress);

        Response.Cookies.Delete(AuthController.RefreshTokenCookieName, new CookieOptions
        {
            HttpOnly = true,
            Secure = true,
            SameSite = SameSiteMode.Lax,
            Path = "/api/v1/auth"
        });

        return Ok(new
        {
            data = new
            {
                status = "anonymized",
                message = "Personal data has been scrubbed and all active tokens revoked."
            }
        });
    }

    private Guid RequireAuthenticatedUserId() =>
        _currentUser.UserId ?? throw new UnauthorizedAccessException("User authentication is required.");
}

[ApiController]
[Route("api/v1/locations/bd")]
public sealed class LocationsController : ControllerBase
{
    private readonly IIdentityService _identityService;

    public LocationsController(IIdentityService identityService)
    {
        _identityService = identityService;
    }

    [HttpGet]
    public IActionResult GetBangladeshLocations([FromQuery] string? level = null, [FromQuery] Guid? parentId = null)
    {
        return Ok(new { data = _identityService.GetBdLocations(level, parentId) });
    }
}

[ApiController]
[Authorize]
[Route("api/v1/sellers/{sellerId:guid}/resources")]
public sealed class SellerIsolationResourcesController : ControllerBase
{
    private readonly IAuthorizationService _authorizationService;

    public SellerIsolationResourcesController(IAuthorizationService authorizationService)
    {
        _authorizationService = authorizationService;
    }

    [HttpGet]
    [RequirePermission(Permissions.SellersManageOwn)]
    public async Task<IActionResult> GetSellerProtectedResource(Guid sellerId)
    {
        var resource = new SellerOwnedResource(Guid.NewGuid(), sellerId, "seller_inventory_vault");
        AuthorizationResult authResult = await _authorizationService.AuthorizeAsync(
            User,
            resource,
            SellerResourceIsolationRequirement.Instance);

        if (!authResult.Succeeded)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = new
                {
                    code = "SELLER_ISOLATION_FORBIDDEN",
                    message = "Cross-seller access denied. Sellers can only access or modify their own resources."
                }
            });
        }

        return Ok(new
        {
            data = new
            {
                sellerId,
                resourceType = resource.ResourceType,
                isolationVerified = true
            }
        });
    }

    [HttpPut]
    [RequirePermission(Permissions.CatalogUpdateOwn)]
    public async Task<IActionResult> UpdateSellerProtectedResource(
        Guid sellerId,
        [FromBody] UpdateSellerResourceRequestDto? request = null)
    {
        var resource = new SellerOwnedResource(Guid.NewGuid(), sellerId, "seller_catalog_listing");
        AuthorizationResult authResult = await _authorizationService.AuthorizeAsync(
            User,
            resource,
            SellerResourceIsolationRequirement.Instance);

        if (!authResult.Succeeded)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new
            {
                error = new
                {
                    code = "SELLER_ISOLATION_FORBIDDEN",
                    message = "Cross-seller modification denied. Sellers cannot modify another seller's resources."
                }
            });
        }

        return Ok(new
        {
            data = new
            {
                sellerId,
                resourceType = resource.ResourceType,
                sku = request?.Sku,
                modified = true,
                isolationVerified = true
            }
        });
    }

    [HttpPost("refund-audit")]
    [RequirePermission(Permissions.OrdersRefund)]
    public IActionResult TriggerOrderRefundAudit(Guid sellerId)
    {
        return Ok(new
        {
            data = new
            {
                sellerId,
                permissionVerified = Permissions.OrdersRefund
            }
        });
    }
}
