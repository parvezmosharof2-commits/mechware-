using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using ShopVerse.Application.Identity;
using ShopVerse.Infrastructure.Identity;

namespace ShopVerse.UnitTests.Identity;

public class BootstrapAdminTests
{
    private static readonly TotpService Totp = new();

    private static IdentityService Create(BootstrapAdminSettings? admin) =>
        new(new PasswordHasherService(),
            new JwtTokenService("unit-test-secret-unit-test-secret-unit-test-secret", "https://api.example.test", "https://example.test", 15),
            Totp,
            new LoggingSmsProvider(NullLogger<LoggingSmsProvider>.Instance),
            IdentitySeedPolicy.None,
            admin);

    [Fact]
    public void NoEmail_MeansNoBootstrapAdmin() =>
        BootstrapAdminSettings.FromValues(null, null, null, null, null).Should().BeNull();

    [Theory]
    [InlineData(null, "JBSWY3DPEHPK3PXPJBSWY3DPEH")]
    [InlineData("Passw0rd!long", null)]
    public void PartlyConfigured_FailsClosed(string? password, string? secret)
    {
        Action act = () => BootstrapAdminSettings.FromValues("a@example.test", null, null, password, secret);
        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("SHORT")]
    [InlineData("not base32 !!!!!!!!!!!!!!!!!!!!!!")]
    [InlineData("jbswy3dpehpk3pxpjbswy3dpeh1")] // 1 is not in the base32 alphabet
    public void WeakOrMalformedTotpSecret_IsRejected(string secret)
    {
        Action act = () => BootstrapAdminSettings.FromValues("a@example.test", null, null, "Passw0rd!long", secret);
        act.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void WeakPassword_IsRejectedAtStartup()
    {
        var settings = BootstrapAdminSettings.FromValues("a@example.test", null, null, "weak", Totp.GenerateBase32Secret(16))!;
        Action act = () => Create(settings);
        act.Should().Throw<Exception>();
    }

    [Fact]
    public void ProvisionedAdmin_AlwaysRequiresTheSecondFactor()
    {
        string secret = Totp.GenerateBase32Secret(16);
        var settings = BootstrapAdminSettings.FromValues("Boss@Example.test", "Boss", null, "Passw0rd!long", secret)!;
        var service = Create(settings);

        var first = service.Login(new LoginRequestDto("boss@example.test", "Passw0rd!long"));
        first.RequiresTwoFactor.Should().BeTrue();

        var second = service.Login(new LoginRequestDto("boss@example.test", "Passw0rd!long", Totp.ComputeTotpCode(secret, DateTimeOffset.UtcNow)));
        second.User.Roles.Should().Contain("admin");
    }
}
