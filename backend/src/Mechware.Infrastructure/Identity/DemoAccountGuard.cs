using Microsoft.EntityFrameworkCore;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Identity;

/// <summary>
/// The demo identities whose credentials are published in this repository. <see cref="IdentitySeeder"/> creates them
/// (Development only) and <see cref="DemoAccountGuard"/> refuses to start anywhere else while one of them exists.
/// </summary>
public static class PublishedDemoCredentials
{
    public const string AdminEmail = "admin@mechware.com.bd";
    public const string SellerAlphaEmail = "seller.alpha@mechware.com.bd";
    public const string SellerBetaEmail = "seller.beta@mechware.com.bd";
    public const string BuyerEmail = "buyer@mechware.com.bd";

    /// <summary>The demo administrator's TOTP secret. Anyone can compute its codes, whatever e-mail it is attached to.</summary>
    public const string AdminTotpSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

    public static IReadOnlyList<string> Emails { get; } = [AdminEmail, SellerAlphaEmail, SellerBetaEmail, BuyerEmail];
}

/// <summary>
/// Not seeding the demo accounts outside Development is not enough: a database that was ever started in Development
/// (a copied dev database, a staging restore, a server briefly run with the wrong environment) keeps them, and the API
/// then signs anyone in as administrator with the published password and TOTP secret. Outside Development the API
/// therefore checks the database at startup and refuses to start while a live account uses a published e-mail or the
/// published TOTP secret. Failing closed is deliberate: silently disabling the accounts would hide that the database
/// came from somewhere it should not have.
/// </summary>
public sealed class DemoAccountGuard(MechwareDbContext db)
{
    /// <summary>E-mails of live accounts that carry a published demo credential; empty when the database is clean.</summary>
    public async Task<IReadOnlyList<string>> FindPublishedCredentialAccountsAsync(CancellationToken cancellationToken = default)
    {
        var emails = PublishedDemoCredentials.Emails;
        return await db.Users
            .Where(u => u.DeletedAtUtc == null
                        && (emails.Contains(u.Email) || u.TwoFactorSecret == PublishedDemoCredentials.AdminTotpSecret))
            .OrderBy(u => u.Email)
            .Select(u => u.Email)
            .ToListAsync(cancellationToken);
    }

    /// <summary>Throws when the database holds an account with a published credential.</summary>
    public async Task EnsureNoPublishedCredentialAccountsAsync(string environmentName, CancellationToken cancellationToken = default)
    {
        var found = await FindPublishedCredentialAccountsAsync(cancellationToken);
        if (found.Count == 0)
        {
            return;
        }

        throw new InvalidOperationException(
            $"Refusing to start in the {environmentName} environment: this database holds {found.Count} account(s) with " +
            $"published demo credentials ({string.Join(", ", found)}). Anyone could sign in to them with the password and " +
            "TOTP secret in the public repository. The database was probably started in Development once. Delete or " +
            "anonymise these accounts, or use a database that has never run in Development, then start again.");
    }
}
