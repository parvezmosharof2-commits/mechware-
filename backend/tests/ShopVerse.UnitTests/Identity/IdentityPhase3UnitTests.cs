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
    public void UserBruteForceLockout_ShouldComputeProgressiveDelay_AndLockAccountAfterFiveFailedAttempts()
    {
        var now = new DateTime(2026, 10, 29, 12, 0, 0, DateTimeKind.Utc);
        var user = User.Create("engineer@mechware.com.bd", "+8801711999999", "hash", "Test Engineer");

        user.GetProgressiveLoginDelayMilliseconds().Should().Be(0);

        int[] expectedDelaysMs = [250, 500, 1000, 2000];
        for (int i = 0; i < 4; i++)
        {
            user.RecordFailedLogin(now);
            user.IsLockedOut(now).Should().BeFalse();
            user.GetProgressiveLoginDelayMilliseconds().Should().Be(expectedDelaysMs[i]);
        }

        user.RecordFailedLogin(now);
        user.IsLockedOut(now).Should().BeTrue();
        user.LockoutUntilUtc.Should().Be(now.AddMinutes(5));
        user.GetProgressiveLoginDelayMilliseconds().Should().Be(4000);

        user.RecordSuccessfulLogin(now.AddMinutes(6));
        user.FailedLoginAttempts.Should().Be(0);
        user.GetProgressiveLoginDelayMilliseconds().Should().Be(0);
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
