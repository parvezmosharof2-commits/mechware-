using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Companies;
using ShopVerse.Infrastructure.Security;

namespace ShopVerse.Infrastructure.Persistence;

/// <summary>
/// The BIN is stored encrypted with a random nonce, so equal BINs no longer look equal in the database. Uniqueness and "is
/// this BIN taken" use this keyed fingerprint instead, kept on every save so it can never drift from the BIN.
/// </summary>
public static class CompanyBinIndex
{
    public const string Property = "BinIndex";

    public static string Of(string bin) => FieldEncryption.Current.BlindIndex(bin);

    /// <summary>The fingerprint under every configured key (current first), for lookups during a key rotation.</summary>
    public static IReadOnlyList<string> AllOf(string bin) => FieldEncryption.Current.BlindIndexes(bin);

    /// <summary>Called by ShopVerseDbContext before every save.</summary>
    public static void Refresh(DbContext db)
    {
        foreach (var entry in db.ChangeTracker.Entries<Company>())
        {
            if (entry.State == EntityState.Added || (entry.State == EntityState.Modified && entry.Property(c => c.Bin).IsModified)
                || entry.Property(Property).CurrentValue is null)
            {
                entry.Property(Property).CurrentValue = Of(entry.Entity.Bin);
            }
        }
    }
}
