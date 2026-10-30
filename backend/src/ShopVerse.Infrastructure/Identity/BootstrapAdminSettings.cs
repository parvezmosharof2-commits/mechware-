using System.Text.RegularExpressions;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// The first administrator of an environment that has no demo accounts. The values come from deployment secrets
/// (never from the repository), and the account is created when the process starts. Two-factor is always on for it:
/// a TOTP secret is required, so there is no way to provision an administrator that a stolen password alone unlocks.
/// </summary>
public sealed record BootstrapAdminSettings(string Email, string FullName, string? Phone, string Password, string TotpSecret)
{
    // 16 bytes of entropy is 26 base32 characters; anything shorter is too guessable to be a second factor.
    private static readonly Regex Base32Secret = new("^[A-Z2-7]{26,}$", RegexOptions.CultureInvariant);

    /// <summary>Returns null when no administrator is configured (the e-mail is empty).</summary>
    public static BootstrapAdminSettings? FromValues(string? email, string? fullName, string? phone, string? password, string? totpSecret)
    {
        if (string.IsNullOrWhiteSpace(email))
        {
            return null;
        }

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(password)) missing.Add("Password");
        if (string.IsNullOrWhiteSpace(totpSecret)) missing.Add("TotpSecret");
        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"Identity:BootstrapAdmin is partly configured; missing {string.Join(", ", missing)}. Set all of them or none.");
        }

        string secret = totpSecret!.Trim().Replace(" ", string.Empty).ToUpperInvariant();
        if (!Base32Secret.IsMatch(secret))
        {
            throw new InvalidOperationException(
                "Identity:BootstrapAdmin:TotpSecret must be a base32 secret of at least 26 characters (A-Z, 2-7).");
        }

        return new BootstrapAdminSettings(
            email.Trim().ToLowerInvariant(),
            string.IsNullOrWhiteSpace(fullName) ? "MECHWARE Administrator" : fullName.Trim(),
            string.IsNullOrWhiteSpace(phone) ? null : phone.Trim(),
            password!,
            secret);
    }
}
