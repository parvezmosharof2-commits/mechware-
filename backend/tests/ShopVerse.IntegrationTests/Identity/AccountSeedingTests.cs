using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Identity;

/// <summary>
/// The demo accounts use passwords and a TOTP secret published in this public repository. They must exist in
/// Development only; anywhere else a published admin would let anyone in. Each class gets its own empty database.
/// </summary>
public class NoDemoAccountsOutsideDevelopmentTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static readonly (string Email, string Password)[] Published =
    [
        ("admin@mechware.com.bd", "MechAdmin#2026!"),
        ("seller.alpha@mechware.com.bd", "SellerAlpha#2026!"),
        ("seller.beta@mechware.com.bd", "SellerBeta#2026!"),
        ("buyer@mechware.com.bd", "BuyerPass#2026!")
    ];

    [PostgresFact]
    public async Task WithoutTheDevelopmentPolicy_NoDemoAccountExists_EveryPublishedCredentialIsRefused()
    {
        await IdentityTestKit.SeedAsync(fixture, IdentitySeedPolicy.ForEnvironment("Production"));

        foreach (var (email, password) in Published)
        {
            Func<Task> login = () => IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(email, password)));
            await login.Should().ThrowAsync<UnauthorizedAccessException>($"{email} must not exist outside Development");
        }

        await using var db = fixture.CreateContext();
        (await db.Users.CountAsync()).Should().Be(0);
    }
}

public class DevelopmentDemoAccountsTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [PostgresFact]
    public async Task Development_SeedsThemOnce_AndRestartsDoNotResetThem()
    {
        await IdentityTestKit.SeedAsync(fixture, IdentitySeedPolicy.ForEnvironment("Development"));

        // A restart seeds again; a changed password must survive it.
        var buyer = (await IdentityTestKit.With(fixture, s => s.GetUsersForAuditAsync())).First(u => u.Email == "buyer@mechware.com.bd");
        await using (var db = fixture.CreateContext())
        {
            var tracked = await db.Users.FirstAsync(u => u.Id == buyer.Id);
            tracked.UpdatePasswordHash(IdentityTestKit.Hasher.HashPassword("Changed#Pass2026!"), DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        await IdentityTestKit.SeedAsync(fixture, IdentitySeedPolicy.ForEnvironment("Development"));

        (await IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto("buyer@mechware.com.bd", "Changed#Pass2026!")))).AccessToken.Should().NotBeNullOrWhiteSpace();
        Func<Task> old = () => IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto("buyer@mechware.com.bd", "BuyerPass#2026!")));
        await old.Should().ThrowAsync<UnauthorizedAccessException>();
        await using var check = fixture.CreateContext();
        (await check.Users.CountAsync(u => u.Email == "buyer@mechware.com.bd")).Should().Be(1);
    }
}

public class BootstrapAdministratorTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    [PostgresFact]
    public async Task ProvisionedAdmin_AlwaysRequiresTheSecondFactor_AndIsStoredInTheDatabase()
    {
        string secret = IdentityTestKit.Totp.GenerateBase32Secret(16);
        var settings = BootstrapAdminSettings.FromValues("Boss@Example.test", "Boss", null, "Passw0rd!long", secret)!;
        await IdentityTestKit.SeedAsync(fixture, IdentitySeedPolicy.None, settings);

        var first = await IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto("boss@example.test", "Passw0rd!long")));
        first.RequiresTwoFactor.Should().BeTrue();

        var second = await IdentityTestKit.With(fixture, s => s.LoginAsync(new LoginRequestDto(
            "boss@example.test", "Passw0rd!long", IdentityTestKit.Totp.ComputeTotpCode(secret, DateTimeOffset.UtcNow))));
        second.User.Roles.Should().Contain("admin");

        await using var db = fixture.CreateContext();
        (await db.AuditLogs.AnyAsync(a => a.Action == "user.bootstrap_admin_provisioned")).Should().BeTrue();
    }

    [PostgresFact]
    public async Task WeakPassword_IsRejectedAtStartup()
    {
        var settings = BootstrapAdminSettings.FromValues("weak@example.test", null, null, "weak", IdentityTestKit.Totp.GenerateBase32Secret(16))!;
        Func<Task> seed = () => IdentityTestKit.SeedAsync(fixture, IdentitySeedPolicy.None, settings);
        await seed.Should().ThrowAsync<ValidationException>();
    }
}
