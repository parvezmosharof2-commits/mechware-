using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// Creates the accounts an environment starts with, in the database. The demo accounts (published credentials) exist
/// only when <see cref="IdentitySeedPolicy.SeedDemoAccounts"/> is set, which only the Development environment does.
/// The bootstrap administrator comes from deployment secrets. Both are idempotent: an account that already exists is
/// left exactly as it is, so a restart never resets a password or a second factor.
/// </summary>
public sealed class IdentitySeeder(ShopVerseDbContext db, IPasswordHasherService passwordHasher, ITotpService totpService)
{
    public async Task SeedAsync(IdentitySeedPolicy policy, BootstrapAdminSettings? bootstrapAdmin, CancellationToken cancellationToken = default)
    {
        if (policy.SeedDemoAccounts)
        {
            await SeedDemoAccountsAsync(cancellationToken);
        }

        if (bootstrapAdmin is not null)
        {
            await SeedBootstrapAdminAsync(bootstrapAdmin, cancellationToken);
        }

        await db.SaveChangesAsync(cancellationToken);
    }

    private async Task<bool> EmailExistsAsync(string email, CancellationToken cancellationToken) =>
        await db.Users.AnyAsync(u => u.Email == email && u.DeletedAtUtc == null, cancellationToken);

    private async Task SeedBootstrapAdminAsync(BootstrapAdminSettings settings, CancellationToken cancellationToken)
    {
        passwordHasher.ValidatePasswordPolicy(settings.Password);

        if (await EmailExistsAsync(settings.Email, cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var admin = User.Create(settings.Email, settings.Phone, passwordHasher.HashPassword(settings.Password), settings.FullName, "en");
        admin.VerifyEmail(now);
        if (settings.Phone is not null)
        {
            admin.VerifyPhone(now);
        }

        admin.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000001"), Permissions.Roles.Admin);
        admin.ConfigureTwoFactorSecret(settings.TotpSecret, JsonSerializer.Serialize(Array.Empty<string>()), now);
        admin.EnableTwoFactor(now);
        db.Users.Add(admin);
        db.AuditLogs.Add(new AuditLog(
            Guid.NewGuid(), admin.Id, null, "user.bootstrap_admin_provisioned", "users", admin.Id.ToString(),
            null, JsonSerializer.Serialize(new { admin.Email }), null, now));
    }

    private async Task SeedDemoAccountsAsync(CancellationToken cancellationToken)
    {
        if (await EmailExistsAsync("admin@mechware.com.bd", cancellationToken))
        {
            return;
        }

        var now = DateTime.UtcNow;

        var admin = User.Create(
            "admin@mechware.com.bd", "+8801711000001", passwordHasher.HashPassword("MechAdmin#2026!"),
            "Parvez Mosharof (Principal Admin)", "en");
        admin.VerifyEmail(now);
        admin.VerifyPhone(now);
        admin.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000001"), Permissions.Roles.Admin);
        var recoveryHashes = new[] { totpService.HashRecoveryCode("ABCDE-12345") };
        admin.ConfigureTwoFactorSecret("JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP", JsonSerializer.Serialize(recoveryHashes), now);
        admin.EnableTwoFactor(now);
        db.Users.Add(admin);

        var sellerAlpha = User.Create(
            "seller.alpha@mechware.com.bd", "+8801711000002", passwordHasher.HashPassword("SellerAlpha#2026!"),
            "Dhaka Heavy Pneumatics Ltd.", "en", IdentityService.SellerAlphaId);
        sellerAlpha.VerifyEmail(now);
        sellerAlpha.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000002"), Permissions.Roles.Seller);
        db.Users.Add(sellerAlpha);

        var sellerBeta = User.Create(
            "seller.beta@mechware.com.bd", "+8801711000003", passwordHasher.HashPassword("SellerBeta#2026!"),
            "Chattogram Marine Automation", "bn", IdentityService.SellerBetaId);
        sellerBeta.VerifyEmail(now);
        sellerBeta.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000002"), Permissions.Roles.Seller);
        db.Users.Add(sellerBeta);

        var buyer = User.Create(
            "buyer@mechware.com.bd", "+8801711000004", passwordHasher.HashPassword("BuyerPass#2026!"),
            "Gazipur Textile Mills Procurement", "en");
        buyer.VerifyEmail(now);
        buyer.VerifyPhone(now);
        buyer.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
        db.Users.Add(buyer);

        db.Addresses.Add(new Address(
            Guid.NewGuid(), buyer.Id, "Factory Plant #1", "Engr. Tanvir Ahmed", "+8801711000004",
            "Plot 42, Sector 7, Tongi Industrial Zone", "Gate 3, Receiving Bay B", "Tongi", "Tongi Industrial Zone",
            "Gazipur", "Dhaka", "1710", true, true, 23.8915m, 90.4023m));
    }
}
