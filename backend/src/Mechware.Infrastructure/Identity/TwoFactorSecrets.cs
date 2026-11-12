using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Security;

namespace Mechware.Infrastructure.Identity;

/// <summary>
/// The authenticator (TOTP) secret and the recovery-code hashes, encrypted at rest with <see cref="FieldEncryption"/>
/// (AES-256-GCM, key from <c>Encryption:FieldKey</c>). A database dump or backup then shows neither the secret, from which
/// anyone could compute every code, nor the recovery-code hashes, whose 40-bit codes are cheap to brute-force offline.
/// <list type="bullet">
/// <item>Each value is bound to its user and its column (GCM associated data), so a ciphertext copied into another row,
/// say an attacker's own secret into an administrator's row, fails to decrypt instead of being accepted there.</item>
/// <item>A value in clear is refused, never read: accepting it would let anyone who can write the column set a secret
/// they know. Values from before this release are encrypted by <see cref="EncryptStoredAsync"/>, which the Worker's
/// <c>migrate</c> deploy step and the API's start-up backfill run.</item>
/// </list>
/// The open methods throw <see cref="CryptographicException"/> for a value that cannot be used (in clear, changed,
/// another user's, or under a key that is not configured).
/// </summary>
public static class TwoFactorSecrets
{
    private const string SecretPurpose = "totp-secret";
    private const string RecoveryCodesPurpose = "recovery-codes";

    public static string SealSecret(Guid userId, string base32Secret) => Seal(userId, SecretPurpose, base32Secret);

    public static string SealRecoveryCodes(Guid userId, string hashesJson) => Seal(userId, RecoveryCodesPurpose, hashesJson);

    /// <summary>The base32 authenticator secret; null when none is stored.</summary>
    public static string? OpenSecret(Guid userId, string? stored) => Open(userId, SecretPurpose, stored);

    /// <summary>The JSON list of recovery-code hashes; null when none is stored.</summary>
    public static string? OpenRecoveryCodes(Guid userId, string? stored) => Open(userId, RecoveryCodesPurpose, stored);

    private static string Seal(Guid userId, string purpose, string value) =>
        FieldEncryption.Current.Protect(value, BoundTo(userId, purpose));

    private static string? Open(Guid userId, string purpose, string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        if (!FieldProtector.IsProtected(stored))
            throw new CryptographicException("The value is stored in clear; the Worker `migrate` step encrypts it.");
        try
        {
            return FieldEncryption.Current.Unprotect(stored, BoundTo(userId, purpose));
        }
        catch (FormatException)
        {
            throw new CryptographicException("The protected value is not valid base64.");
        }
    }

    private static byte[] BoundTo(Guid userId, string purpose) => Encoding.UTF8.GetBytes($"mechware/{purpose}/v1/{userId:N}");

    /// <summary>What a run of <see cref="EncryptStoredAsync"/> did.</summary>
    /// <param name="Rewritten">Users whose secret or recovery codes were encrypted, or re-encrypted under the current key.</param>
    /// <param name="Unreadable">Users with a stored value that does not decrypt for them; left as they are.</param>
    public sealed record ConversionResult(int Rewritten, int Unreadable);

    /// <summary>
    /// Brings every stored secret and recovery-code list under the current key, bound to its user: values in clear (written
    /// before encryption) and values under a previous key after a rotation. Idempotent: a second run rewrites nothing. A row
    /// changed meanwhile (a new enrolment) is skipped, since the application already wrote it encrypted.
    /// </summary>
    public static async Task<ConversionResult> EncryptStoredAsync(MechwareDbContext db, CancellationToken cancellationToken, int batchSize = 200)
    {
        string like = FieldEncryption.Current.CurrentPrefix + "%";
        int rewritten = 0, unreadable = 0;
        Guid after = Guid.Empty;
        while (true)
        {
            var rows = await db.Database.SqlQuery<StoredRow>($"""
                SELECT id AS "Id", two_factor_secret AS "Secret", two_factor_recovery_codes_json AS "RecoveryCodes" FROM users
                 WHERE ((two_factor_secret IS NOT NULL AND two_factor_secret NOT LIKE {like})
                        OR (two_factor_recovery_codes_json IS NOT NULL AND two_factor_recovery_codes_json NOT LIKE {like}))
                   AND id > {after}
                 ORDER BY id
                 LIMIT {batchSize}
                """).ToListAsync(cancellationToken);
            if (rows.Count == 0) break;
            after = rows[^1].Id;

            foreach (var row in rows)
            {
                bool ok = TryReseal(row.Id, SecretPurpose, row.Secret, out string? secret)
                          & TryReseal(row.Id, RecoveryCodesPurpose, row.RecoveryCodes, out string? codes);
                if (!ok) unreadable++;

                int changed = 0;
                if (secret != row.Secret)
                {
                    changed += await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE users SET two_factor_secret = {secret} WHERE id = {row.Id} AND two_factor_secret = {row.Secret}", cancellationToken);
                }
                if (codes != row.RecoveryCodes)
                {
                    changed += await db.Database.ExecuteSqlInterpolatedAsync(
                        $"UPDATE users SET two_factor_recovery_codes_json = {codes} WHERE id = {row.Id} AND two_factor_recovery_codes_json = {row.RecoveryCodes}",
                        cancellationToken);
                }
                if (changed > 0) rewritten++;
            }
        }
        return new ConversionResult(rewritten, unreadable);
    }

    /// <summary>The value to store: unchanged when already current (or unreadable, then false), else sealed under the current key.</summary>
    private static bool TryReseal(Guid userId, string purpose, string? stored, out string? result)
    {
        result = stored;
        if (stored is null || FieldEncryption.Current.IsCurrent(stored)) return true;
        try
        {
            string plain = FieldProtector.IsProtected(stored) ? Open(userId, purpose, stored)! : stored;
            result = Seal(userId, purpose, plain);
            return true;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    internal sealed class StoredRow
    {
        public Guid Id { get; set; }
        public string? Secret { get; set; }
        public string? RecoveryCodes { get; set; }
    }
}
