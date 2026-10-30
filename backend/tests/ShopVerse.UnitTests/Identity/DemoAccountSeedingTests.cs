using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShopVerse.Application.Identity;
using ShopVerse.Infrastructure.Identity;

namespace ShopVerse.UnitTests.Identity;

/// <summary>
/// The demo accounts use passwords and a TOTP secret published in this (public) repository. They must exist in
/// Development only; anywhere else a published admin would let anyone in.
/// </summary>
public class DemoAccountSeedingTests
{
    private static readonly (string Email, string Password)[] DemoAccounts =
    [
        ("admin@mechware.com.bd", "MechAdmin#2026!"),
        ("seller.alpha@mechware.com.bd", "SellerAlpha#2026!"),
        ("seller.beta@mechware.com.bd", "SellerBeta#2026!"),
        ("buyer@mechware.com.bd", "BuyerPass#2026!")
    ];

    private static IdentityService Create(IdentitySeedPolicy policy)
    {
        var hasher = new PasswordHasherService();
        var tokens = new JwtTokenService(
            "unit-test-secret-unit-test-secret-unit-test-secret", "https://api.example.test", "https://example.test", 15);
        return new IdentityService(hasher, tokens, new TotpService(), new LoggingSmsProvider(NullLogger<LoggingSmsProvider>.Instance), policy);
    }

    [Theory]
    [InlineData("Development", true)]
    [InlineData("development", true)]
    [InlineData("Production", false)]
    [InlineData("production", false)]
    [InlineData("Staging", false)]
    [InlineData("Testing", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void OnlyTheDevelopmentEnvironmentSeedsDemoAccounts(string? environment, bool seeds) =>
        IdentitySeedPolicy.ForEnvironment(environment).SeedDemoAccounts.Should().Be(seeds);

    [Fact]
    public void WithoutTheDevelopmentPolicy_NoDemoAccountExists_EveryPublishedCredentialIsRefused()
    {
        var service = Create(IdentitySeedPolicy.None);
        foreach (var (email, password) in DemoAccounts)
        {
            Action login = () => service.Login(new LoginRequestDto(email, password));
            login.Should().Throw<UnauthorizedAccessException>($"{email} must not exist outside Development");
        }
    }

    [Fact]
    public void WithoutTheDevelopmentPolicy_TheAdminCannotBeReachedEvenWithThePublishedTotpSecret()
    {
        var service = Create(IdentitySeedPolicy.None);
        var totp = new TotpService();
        string code = totp.ComputeTotpCode("JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP", DateTimeOffset.UtcNow);

        Action login = () => service.Login(new LoginRequestDto("admin@mechware.com.bd", "MechAdmin#2026!", code));
        login.Should().Throw<UnauthorizedAccessException>();
    }

    [Fact]
    public void WithTheDevelopmentPolicy_TheDemoAdminExists_SoThatTheCheckAboveIsMeaningful()
    {
        var service = Create(new IdentitySeedPolicy(true));
        var result = service.Login(new LoginRequestDto("admin@mechware.com.bd", "MechAdmin#2026!"));
        result.RequiresTwoFactor.Should().BeTrue();
    }
}
