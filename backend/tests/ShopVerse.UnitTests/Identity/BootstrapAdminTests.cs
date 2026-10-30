using FluentAssertions;
using ShopVerse.Infrastructure.Identity;

namespace ShopVerse.UnitTests.Identity;

public class BootstrapAdminTests
{
    private static readonly TotpService Totp = new();

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
    public void ValidValues_AreNormalised()
    {
        var settings = BootstrapAdminSettings.FromValues(" Boss@Example.test ", null, null, "Passw0rd!long", Totp.GenerateBase32Secret(16).ToLowerInvariant())!;
        settings.Email.Should().Be("boss@example.test");
        settings.TotpSecret.Should().MatchRegex("^[A-Z2-7]{26,}$");
        settings.FullName.Should().NotBeNullOrWhiteSpace();
    }
}
