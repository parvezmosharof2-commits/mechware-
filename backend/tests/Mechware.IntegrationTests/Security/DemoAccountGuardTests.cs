using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Security;

/// <summary>
/// Finding #1 of the 2026-11-04 independent test (High): a database that was ever started in Development keeps the
/// demo accounts, and a Production API on it signed the published admin in with full rights. Not seeding is not enough;
/// the API must refuse to start outside Development while such an account exists.
/// </summary>
public sealed class DemoAccountGuardTests
{
    private static DbBackedFactory Factory(string environment) =>
        new(environment, new Dictionary<string, string>
        {
            ["Jwt:Secret"] = "prod-like-secret-" + Guid.NewGuid().ToString("N"),
            ["Storage:AccessKey"] = "test-access-key",
            ["Encryption:FieldKey"] = TestEncryption.KeyBase64,
            ["Encryption:PreviousFieldKeys"] = TestEncryption.PreviousKeyBase64,
            ["Storage:SecretKey"] = "test-secret-key",
            ["Documents:LocalPath"] = TestEncryption.DocumentsPath,
            ["Antivirus:Host"] = "127.0.0.1",
            ["Email:Host"] = "localhost",
            ["Email:FromAddress"] = "no-reply@mechware.test",
            ["Search:ApiKey"] = "test-search-key"
        });

    private static MechwareDbContext Db(DbBackedFactory factory) =>
        new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    /// <summary>What a Development start does to a database: the published demo accounts are created in it.</summary>
    private static async Task SeedAsIfDevelopmentRanAsync(DbBackedFactory factory)
    {
        await using var db = Db(factory);
        await new IdentitySeeder(db, new PasswordHasherService(), new TotpService())
            .SeedAsync(IdentitySeedPolicy.ForEnvironment("Development"), bootstrapAdmin: null);
    }

    [PostgresTheory]
    [InlineData("Production")]
    [InlineData("Staging")]
    public async Task RefusesToStart_WhenTheDatabaseStillHoldsTheDemoAccounts(string environment)
    {
        await using var factory = Factory(environment);
        await SeedAsIfDevelopmentRanAsync(factory);

        FluentActions.Invoking(() => factory.CreateClient()).Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("published demo credentials")
                        && e.ToString().Contains(PublishedDemoCredentials.AdminEmail));
    }

    [PostgresFact]
    public async Task RefusesToStart_WhenThePublishedTotpSecretIsOnARenamedAccount()
    {
        await using var factory = Factory("Production");
        await SeedAsIfDevelopmentRanAsync(factory);
        await using (var db = Db(factory))
        {
            // Rename every demo account: the published TOTP secret alone still lets anyone pass the second factor.
            await db.Database.ExecuteSqlRawAsync("UPDATE users SET email = 'renamed-' || email");
        }

        FluentActions.Invoking(() => factory.CreateClient()).Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("renamed-" + PublishedDemoCredentials.AdminEmail));
    }

    [PostgresFact]
    public async Task Starts_WhenTheDemoAccountsWereAnonymised()
    {
        await using var factory = Factory("Production");
        await SeedAsIfDevelopmentRanAsync(factory);
        await using (var db = Db(factory))
        {
            foreach (var user in await db.Users.ToListAsync())
            {
                user.AnonymizeForGdpr(DateTime.UtcNow);
            }

            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        (await client.GetAsync("/health/live")).IsSuccessStatusCode.Should().BeTrue();
    }

    /// <summary>The control: Development seeds the accounts and must still start, or the check above proves nothing.</summary>
    [PostgresFact]
    public async Task Development_StillStarts_WithItsDemoAccounts()
    {
        await using var factory = new DbBackedFactory("Development");
        var client = factory.CreateClient();

        (await client.GetAsync("/health/live")).IsSuccessStatusCode.Should().BeTrue();
        await using var db = Db(factory);
        (await new DemoAccountGuard(db).FindPublishedCredentialAccountsAsync()).Should().HaveCount(4);
    }
}
