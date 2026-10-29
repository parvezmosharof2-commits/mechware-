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
    public IActionResult VerifyOtp([FromBody] VerifyOtpRequestDto request)
    {
        AuthTokenResponseDto result = _identityService.VerifyOtp(
            request,
            _currentUser.IpAddress,
            _currentUser.UserAgent);

        WriteRefreshCookieIfPresent(result.RefreshTokenCookieValue);
        return Ok(new { data = MapTokenEnvelope(result) });
    }

    [HttpPost("login")]
    [EnableRateLimiting(AuthStrictRateLimitPolicy)]
    public IActionResult Login([FromBody] LoginRequestDto request)
    {
        AuthTokenResponseDto result = _identityService.Login(
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
    public IActionResult GoogleLogin([FromBody] GoogleLoginRequestDto request)
    {
        AuthTokenResponseDto result = _identityService.GoogleLogin(
            request,
            _currentUser.IpAddress,
            _currentUser.UserAgent);

        WriteRefreshCookieIfPresent(result.RefreshTokenCookieValue);
        return Ok(new { data = MapTokenEnvelope(result) });
    }

    [HttpPost("refresh")]
    public IActionResult Refresh()
    {
        string? rawRefreshToken = Request.Cookies[RefreshTokenCookieName];
        AuthTokenResponseDto result = _identityService.RotateRefreshToken(
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
    public IActionResult Logout()
    {
        string? rawRefreshToken = Request.Cookies[RefreshTokenCookieName];
        bool revokedByCookie = _identityService.RevokeRefreshTokenByCookie(rawRefreshToken, _currentUser.IpAddress);

        if (!revokedByCookie && _currentUser.UserId.HasValue)
        {
            _identityService.RevokeSessions(
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
    public IActionResult ForgotPassword([FromBody] ForgotPasswordRequestDto request)
    {
        (string referenceId, _) = _identityService.InitiatePasswordReset(
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
    public IActionResult ResetPassword([FromBody] ResetPasswordRequestDto request)
    {
        _identityService.CompletePasswordReset(request, _currentUser.IpAddress);
        return Ok(new { data = new { passwordReset = true } });
    }

    [Authorize]
    [HttpPost("2fa/setup")]
    public IActionResult SetupTwoFactor()
    {
        Guid userId = RequireAuthenticatedUserId();
        TotpSetupResponseDto result = _identityService.SetupTwoFactor(userId, _currentUser.IpAddress);

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
    public IActionResult VerifyTwoFactor([FromBody] TotpVerifyRequestDto request)
    {
        Guid userId = RequireAuthenticatedUserId();
        _identityService.VerifyAndEnableTwoFactor(userId, request.Code, _currentUser.IpAddress);
        return Ok(new { data = new { twoFactorEnabled = true } });
    }

    [Authorize]
    [HttpGet("sessions")]
    public IActionResult GetSessions()
    {
        Guid userId = RequireAuthenticatedUserId();
        string? rawRefreshToken = Request.Cookies[RefreshTokenCookieName];
        IReadOnlyList<ActiveSessionDto> sessions = _identityService.GetActiveSessions(userId, rawRefreshToken);
        return Ok(new { data = sessions });
    }

    [Authorize]
    [HttpPost("sessions/revoke")]
    public IActionResult RevokeSessions([FromBody] RevokeSessionsRequestDto request)
    {
        Guid userId = RequireAuthenticatedUserId();
        string? rawRefreshToken = Request.Cookies[RefreshTokenCookieName];
        int revokedCount = _identityService.RevokeSessions(userId, request, _currentUser.IpAddress, rawRefreshToken);
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
    public IActionResult GetMe()
    {
        Guid userId = RequireAuthenticatedUserId();
        return Ok(new { data = _identityService.GetUserProfile(userId) });
    }

    [HttpGet("addresses")]
    public IActionResult GetAddresses()
    {
        Guid userId = RequireAuthenticatedUserId();
        return Ok(new { data = _identityService.GetAddresses(userId) });
    }

    [HttpPost("addresses")]
    public IActionResult CreateAddress([FromBody] UpsertAddressRequestDto request)
    {
        Guid userId = RequireAuthenticatedUserId();
        AddressDto created = _identityService.CreateAddress(userId, request, _currentUser.IpAddress);
        return StatusCode(StatusCodes.Status201Created, new { data = created });
    }

    [HttpPut("addresses/{addressId:guid}")]
    public IActionResult UpdateAddress(Guid addressId, [FromBody] UpsertAddressRequestDto request)
    {
        Guid userId = RequireAuthenticatedUserId();
        AddressDto updated = _identityService.UpdateAddress(userId, addressId, request, _currentUser.IpAddress);
        return Ok(new { data = updated });
    }

    [HttpDelete("addresses/{addressId:guid}")]
    public IActionResult DeleteAddress(Guid addressId)
    {
        Guid userId = RequireAuthenticatedUserId();
        _identityService.DeleteAddress(userId, addressId, _currentUser.IpAddress);
        return NoContent();
    }

    [HttpGet("export")]
    public IActionResult ExportGdprBundle()
    {
        Guid userId = RequireAuthenticatedUserId();
        GdprExportBundleDto bundle = _identityService.ExportUserData(userId, _currentUser.IpAddress);
        return Ok(new { data = bundle });
    }

    [HttpDelete]
    public IActionResult AnonymizeAccount()
    {
        Guid userId = RequireAuthenticatedUserId();
        _identityService.AnonymizeAndEraseUser(userId, _currentUser.IpAddress);

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
    public async Task<IActionResult> UpdateSellerProtectedResource(Guid sellerId)
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
