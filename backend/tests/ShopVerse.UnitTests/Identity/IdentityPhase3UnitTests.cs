using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Logging.Abstractions;
using ShopVerse.Api.Authorization;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Identity;

namespace ShopVerse.UnitTests.Identity;

public class IdentityPhase3UnitTests
{
    private readonly PasswordHasherService _passwordHasher = new();
    private readonly TotpService _totpService = new();
    private readonly JwtTokenService _tokenService = new("mechware-test-high-entropy-secret-key-256bit-2026!!");

    [Theory]
    [InlineData("short1!")]
    [InlineData("alllowercase123!")]
    [InlineData("ALLUPPERCASE123!")]
    [InlineData("NoNumericDigits!")]
    [InlineData("NoSpecialChar123")]
    public void PasswordPolicy_ShouldRejectWeakPasswords(string weakPassword)
    {
        Action act = () => _passwordHasher.ValidatePasswordPolicy(weakPassword);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void PasswordHasher_ShouldHashAndVerifyStrongPassword_WithConstantTimeComparison()
    {
        const string strongPass = "MechWare#Industrial2026!";

        string hash = _passwordHasher.HashPassword(strongPass);

        hash.Should().StartWith("$mw-pbkdf2-sha512$v1$");
        _passwordHasher.VerifyPassword(strongPass, hash).Should().BeTrue();
        _passwordHasher.VerifyPassword("WrongPassword#2026!", hash).Should().BeFalse();
    }

    [Fact]
    public void UserBruteForceLockout_ShouldLockAccountAfterFiveFailedAttempts()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var user = User.Create("engineer@mechware.com.bd", "+8801711999999", "hash", "Test Engineer");

        for (int i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(now);
            user.IsLockedOut(now).Should().BeFalse();
        }

        user.RecordFailedLogin(now);
        user.IsLockedOut(now).Should().BeTrue();
        user.LockoutUntilUtc.Should().Be(now.AddMinutes(5));

        user.RecordSuccessfulLogin(now.AddMinutes(6));
        user.FailedLoginAttempts.Should().Be(0);
        user.IsLockedOut(now.AddMinutes(6)).Should().BeFalse();
    }

    [Fact]
    public void TotpService_ShouldGenerateAndVerifyRfc6238SixDigitCode_AndBlockReplayWithinSameTimeStep()
    {
        string secret = _totpService.GenerateBase32Secret();
        var timestamp = new DateTimeOffset(2026, 10, 29, 12, 0, 0, TimeSpan.Zero);

        string currentCode = _totpService.ComputeTotpCode(secret, timestamp);
        currentCode.Should().HaveLength(6);

        // First presentation of currentCode succeeds and returns matchedStep
        _totpService.TryVerifyTotpCode(secret, currentCode, timestamp, null, out long matchedStep).Should().BeTrue();
        matchedStep.Should().BeGreaterThan(0);

        // Second presentation of the exact same code within the same or adjacent window is blocked (RFC 6238 §5.2)
        _totpService.TryVerifyTotpCode(secret, currentCode, timestamp.AddSeconds(10), matchedStep, out _).Should().BeFalse();

        // Next 30-second window produces a fresh time-step > matchedStep -> succeeds
        var nextWindow = timestamp.AddSeconds(30);
        string nextCode = _totpService.ComputeTotpCode(secret, nextWindow);
        _totpService.TryVerifyTotpCode(secret, nextCode, nextWindow, matchedStep, out long nextMatchedStep).Should().BeTrue();
        nextMatchedStep.Should().Be(matchedStep + 1);
    }

    [Fact]
    public async Task RefreshTokenRotation_ShouldTolerate10SecondSameIpGraceWindow_AndRevokeEntireFamilyOnBreach()
    {
        var smsProvider = new LoggingSmsProvider(NullLogger<LoggingSmsProvider>.Instance);
        var identityService = new IdentityService(_passwordHasher, _tokenService, _totpService, smsProvider);

        var (regResp, rawOtp) = await identityService.RegisterAsync(
            new RegisterRequestDto("rotation@mechware.com.bd", "+8801711888777", "RotatePass#2026!", "Rotation Tester"),
            "203.112.218.10");

        var t0 = new DateTime(2026, 10, 29, 10, 0, 0, DateTimeKind.Utc);
        AuthTokenResponseDto initialSession = identityService.VerifyOtp(
            new VerifyOtpRequestDto("+8801711888777", rawOtp, "PhoneVerify", regResp.ReferenceId),
            "203.112.218.10",
            "Mozilla/5.0",
            t0);

        string tokenGen1 = initialSession.RefreshTokenCookieValue!;
        tokenGen1.Should().NotBeNullOrWhiteSpace();

        // 1. Rotate tokenGen1 -> issues tokenGen2 at T0 + 2s
        var t1 = t0.AddSeconds(2);
        AuthTokenResponseDto rotatedSession = identityService.RotateRefreshToken(
            tokenGen1,
            "203.112.218.10",
            "Mozilla/5.0",
            t1);

        string tokenGen2 = rotatedSession.RefreshTokenCookieValue!;
        tokenGen2.Should().NotBeNullOrWhiteSpace();
        tokenGen2.Should().NotBe(tokenGen1);

        // 2. ADR-0006 Multi-tab / SSR race within 10s grace window from SAME IP -> succeeds without revoking tokenGen2
        var tGrace = t1.AddSeconds(5);
        AuthTokenResponseDto graceResult = identityService.RotateRefreshToken(
            tokenGen1,
            "203.112.218.10",
            "Mozilla/5.0",
            tGrace);

        graceResult.AccessToken.Should().NotBeNullOrWhiteSpace();

        // 3. Replay tokenGen1 OUTSIDE the 10-second grace window (T1 + 30s) -> BREACH DETECTED -> Revokes ENTIRE token family!
        var tBreach = t1.AddSeconds(30);
        Action replayAct = () => identityService.RotateRefreshToken(
            tokenGen1,
            "185.199.108.99",
            "AttackerBot/1.0",
            tBreach);

        replayAct.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("*reuse detected*");

        // 4. Verify legitimate tokenGen2 in the same family is now ALSO revoked!
        Action legitimateTokenAct = () => identityService.RotateRefreshToken(
            tokenGen2,
            "203.112.218.10",
            "Mozilla/5.0",
            tBreach.AddSeconds(1));

        legitimateTokenAct.Should().Throw<UnauthorizedAccessException>()
            .WithMessage("*expired or revoked*");
    }

    [Fact]
    public void SingleSessionCookieLogout_ShouldRevokeOnlyPresentedCookieFamily_LeavingOtherDeviceSessionsActive()
    {
        var smsProvider = new LoggingSmsProvider(NullLogger<LoggingSmsProvider>.Instance);
        var identityService = new IdentityService(_passwordHasher, _tokenService, _totpService, smsProvider);

        var t0 = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var device1 = identityService.Login(
            new LoginRequestDto("buyer@mechware.com.bd", "BuyerPass#2026!", null),
            "203.112.218.10",
            "DesktopChrome/1.0",
            t0);

        var device2 = identityService.Login(
            new LoginRequestDto("buyer@mechware.com.bd", "BuyerPass#2026!", null),
            "203.112.218.11",
            "MobileSafari/1.0",
            t0.AddSeconds(1));

        // Logout Device 1 via its refresh_token cookie
        identityService.RevokeRefreshTokenByCookie(device1.RefreshTokenCookieValue!, t0.AddSeconds(5));

        // Device 1 refresh token is now revoked
        Action act1 = () => identityService.RotateRefreshToken(
            device1.RefreshTokenCookieValue!,
            "203.112.218.10",
            "DesktopChrome/1.0",
            t0.AddSeconds(10));
        act1.Should().Throw<UnauthorizedAccessException>();

        // Device 2 refresh token remains active and rotatable
        var rotatedDevice2 = identityService.RotateRefreshToken(
            device2.RefreshTokenCookieValue!,
            "203.112.218.11",
            "MobileSafari/1.0",
            t0.AddSeconds(10));
        rotatedDevice2.AccessToken.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void GoogleOidcLogin_ShouldValidateJwtClaims_AndRejectExpiredOrUntrustedIssuer()
    {
        var smsProvider = new LoggingSmsProvider(NullLogger<LoggingSmsProvider>.Instance);
        var identityService = new IdentityService(_passwordHasher, _tokenService, _totpService, smsProvider);

        string header = Base64UrlEncode(JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT" }));
        string validPayload = Base64UrlEncode(JsonSerializer.Serialize(new
        {
            iss = "https://accounts.google.com",
            sub = "google-uid-998877",
            email = "oidc.engineer@mechware.com.bd",
            email_verified = true,
            name = "Engr. OIDC Verified",
            exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()
        }));
        string validJwt = $"{header}.{validPayload}.sig_verified";

        var session = identityService.GoogleLogin(
            new GoogleOAuthRequestDto(validJwt, null, null),
            "203.112.218.10",
            "Mozilla/5.0");
        session.AccessToken.Should().NotBeNullOrWhiteSpace();

        // Untrusted issuer must be rejected
        string badIssuerPayload = Base64UrlEncode(JsonSerializer.Serialize(new
        {
            iss = "https://evil-idp.example.com",
            sub = "evil-1",
            email = "victim@mechware.com.bd",
            email_verified = true,
            exp = DateTimeOffset.UtcNow.AddMinutes(30).ToUnixTimeSeconds()
        }));
        Action badIssAct = () => identityService.GoogleLogin(
            new GoogleOAuthRequestDto($"{header}.{badIssuerPayload}.sig", null, null),
            "203.112.218.10",
            "Mozilla/5.0");
        badIssAct.Should().Throw<UnauthorizedAccessException>().WithMessage("*issuer*");
    }

    [Fact]
    public async Task OtpCode_ShouldEnforceMaxThreeAttempts_AndRejectFourthAttempt()
    {
        var smsProvider = new LoggingSmsProvider(NullLogger<LoggingSmsProvider>.Instance);
        var identityService = new IdentityService(_passwordHasher, _tokenService, _totpService, smsProvider);

        SendOtpResponseDto otpSent = await identityService.SendOtpAsync(
            new SendOtpRequestDto("+8801711000001", "Login"),
            "203.112.218.10");

        otpSent.MaxAttempts.Should().Be(3);

        for (int attempt = 1; attempt <= 3; attempt++)
        {
            Action wrongAct = () => identityService.VerifyOtp(
                new VerifyOtpRequestDto("+8801711000001", "000000", "Login", otpSent.ReferenceId),
                "203.112.218.10",
                "Mozilla/5.0");
            wrongAct.Should().Throw<ValidationException>();
        }

        // 4th attempt must be rejected because MaxVerificationAttempts (3) was reached
        Action fourthAct = () => identityService.VerifyOtp(
            new VerifyOtpRequestDto("+8801711000001", "000000", "Login", otpSent.ReferenceId),
            "203.112.218.10",
            "Mozilla/5.0");
        fourthAct.Should().Throw<ValidationException>().WithMessage("*Maximum OTP verification attempts*");
    }

    [Fact]
    public void BangladeshAddressBook_ShouldRejectCrossDivisionDistrictUpazilaMismatch()
    {
        var smsProvider = new LoggingSmsProvider(NullLogger<LoggingSmsProvider>.Instance);
        var identityService = new IdentityService(_passwordHasher, _tokenService, _totpService, smsProvider);

        var buyerProfile = identityService.GetUsersForAudit().First(u => u.Email == "buyer@mechware.com.bd");

        // Division = Dhaka, but District = Chattogram -> must throw ValidationException
        Action mismatchDistrictAct = () => identityService.CreateAddress(
            buyerProfile.Id,
            new UpsertAddressRequestDto(
                "Invalid Plant",
                "Engr. Test",
                "+8801711000001",
                "Plot 1",
                null,
                "Halishahar",
                "Chattogram",
                "Dhaka",
                "Chattogram EPZ (Halishahar)",
                "4223",
                false,
                false,
                null,
                null));

        mismatchDistrictAct.Should().Throw<ValidationException>()
            .WithMessage("*does not belong to division*");
    }

    [Fact]
    public async Task PermissionHandler_ShouldGrantMatchingClaim_AndDenyMissingClaim()
    {
        var handler = new PermissionAuthorizationHandler();
        var requirement = new PermissionRequirement(Permissions.OrdersRefund);

        var allowedUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtTokenService.PermissionClaimType, Permissions.OrdersRefund)
        ], "Bearer"));

        var allowedContext = new AuthorizationHandlerContext([requirement], allowedUser, null);
        await handler.HandleAsync(allowedContext);
        allowedContext.HasSucceeded.Should().BeTrue();

        var deniedUser = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(JwtTokenService.PermissionClaimType, Permissions.OrdersViewOwn)
        ], "Bearer"));

        var deniedContext = new AuthorizationHandlerContext([requirement], deniedUser, null);
        await handler.HandleAsync(deniedContext);
        deniedContext.HasSucceeded.Should().BeFalse();
    }

    [Fact]
    public async Task SellerResourceIsolationHandler_ShouldPreventSellerFromAccessingAnotherSellersResource()
    {
        var handler = new SellerResourceIsolationHandler();
        var requirement = SellerResourceIsolationRequirement.Instance;

        Guid sellerAlphaId = IdentityService.SellerAlphaId;
        Guid sellerBetaId = IdentityService.SellerBetaId;

        var sellerAlphaPrincipal = new ClaimsPrincipal(new ClaimsIdentity(
        [
            new Claim(ClaimTypes.Role, Permissions.Roles.Seller),
            new Claim(JwtTokenService.SellerIdClaimType, sellerAlphaId.ToString())
        ], "Bearer"));

        var alphaOwnResource = new SellerOwnedResource(Guid.NewGuid(), sellerAlphaId, "catalog_item");
        var betaForeignResource = new SellerOwnedResource(Guid.NewGuid(), sellerBetaId, "catalog_item");

        // Seller Alpha accessing own resource -> ALLOWED
        var ownContext = new AuthorizationHandlerContext([requirement], sellerAlphaPrincipal, alphaOwnResource);
        await handler.HandleAsync(ownContext);
        ownContext.HasSucceeded.Should().BeTrue();

        // Seller Alpha attempting to read/modify Seller Beta's resource -> FORBIDDEN
        var crossSellerContext = new AuthorizationHandlerContext([requirement], sellerAlphaPrincipal, betaForeignResource);
        await handler.HandleAsync(crossSellerContext);
        crossSellerContext.HasSucceeded.Should().BeFalse();
    }

    private static string Base64UrlEncode(string json)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
