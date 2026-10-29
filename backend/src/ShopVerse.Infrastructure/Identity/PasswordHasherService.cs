using System.Security.Cryptography;
using System.Text;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// Enterprise password hasher and NIST/OWASP password policy validator.
/// Uses 210,000-iteration PBKDF2-HMAC-SHA512 with 128-bit cryptographic salt and constant-time comparison.
/// </summary>
public sealed class PasswordHasherService : IPasswordHasherService
{
    private const int SaltSize = 16;
    private const int KeySize = 64;
    private const int Iterations = 210_000;
    private const string VersionPrefix = "$mw-pbkdf2-sha512$v1$";

    public void ValidatePasswordPolicy(string password)
    {
        var errors = new List<ValidationError>();

        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ValidationException("password", "Password is required.");
        }

        if (password.Length < 8)
        {
            errors.Add(new ValidationError("password", "Password must be at least 8 characters long."));
        }

        if (password.Length > 128)
        {
            errors.Add(new ValidationError("password", "Password must not exceed 128 characters."));
        }

        if (!password.Any(char.IsUpper))
        {
            errors.Add(new ValidationError("password", "Password must contain at least one uppercase letter."));
        }

        if (!password.Any(char.IsLower))
        {
            errors.Add(new ValidationError("password", "Password must contain at least one lowercase letter."));
        }

        if (!password.Any(char.IsDigit))
        {
            errors.Add(new ValidationError("password", "Password must contain at least one numeric digit."));
        }

        if (!password.Any(ch => !char.IsLetterOrDigit(ch)))
        {
            errors.Add(new ValidationError("password", "Password must contain at least one special character."));
        }

        if (errors.Count > 0)
        {
            throw new ValidationException(errors);
        }
    }

    public string HashPassword(string password)
    {
        ValidatePasswordPolicy(password);

        byte[] salt = RandomNumberGenerator.GetBytes(SaltSize);
        byte[] subkey = Rfc2898DeriveBytes.Pbkdf2(
            Encoding.UTF8.GetBytes(password),
            salt,
            Iterations,
            HashAlgorithmName.SHA512,
            KeySize);

        return $"{VersionPrefix}{Iterations}${Convert.ToBase64String(salt)}${Convert.ToBase64String(subkey)}";
    }

    public bool VerifyPassword(string password, string passwordHash)
    {
        if (string.IsNullOrEmpty(password) || string.IsNullOrEmpty(passwordHash))
        {
            return false;
        }

        if (!passwordHash.StartsWith(VersionPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        string payload = passwordHash[VersionPrefix.Length..];
        string[] parts = payload.Split('$');
        if (parts.Length != 3 || !int.TryParse(parts[0], out int iterations) || iterations < 10_000)
        {
            return false;
        }

        try
        {
            byte[] salt = Convert.FromBase64String(parts[1]);
            byte[] expectedSubkey = Convert.FromBase64String(parts[2]);

            byte[] actualSubkey = Rfc2898DeriveBytes.Pbkdf2(
                Encoding.UTF8.GetBytes(password),
                salt,
                iterations,
                HashAlgorithmName.SHA512,
                expectedSubkey.Length);

            return CryptographicOperations.FixedTimeEquals(actualSubkey, expectedSubkey);
        }
        catch (FormatException)
        {
            return false;
        }
    }
}
