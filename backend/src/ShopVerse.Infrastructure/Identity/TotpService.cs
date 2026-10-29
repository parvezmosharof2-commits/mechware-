using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using ShopVerse.Application.Identity;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// RFC 6238 Time-Based One-Time Password (TOTP) and single-use backup recovery code service.
/// </summary>
public sealed class TotpService : ITotpService
{
    private const string Base32Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";
    private const int TimeStepSeconds = 30;
    private const int CodeDigits = 6;

    public string GenerateBase32Secret(int byteLength = 20)
    {
        byte[] bytes = RandomNumberGenerator.GetBytes(byteLength);
        return ToBase32(bytes);
    }

    public string BuildOtpAuthUri(string email, string base32Secret, string issuer = "MECHWARE")
    {
        string encodedIssuer = Uri.EscapeDataString(issuer);
        string encodedEmail = Uri.EscapeDataString(email.Trim().ToLowerInvariant());
        return $"otpauth://totp/{encodedIssuer}:{encodedEmail}?secret={base32Secret}&issuer={encodedIssuer}&algorithm=SHA1&digits={CodeDigits}&period={TimeStepSeconds}";
    }

    public string ComputeTotpCode(string base32Secret, DateTimeOffset timestamp)
    {
        byte[] key = FromBase32(base32Secret);
        long counter = timestamp.ToUnixTimeSeconds() / TimeStepSeconds;
        return ComputeHotp(key, counter);
    }

    public bool VerifyTotpCode(string base32Secret, string code, DateTimeOffset timestamp, int windowSteps = 1)
    {
        if (string.IsNullOrWhiteSpace(base32Secret) || string.IsNullOrWhiteSpace(code))
        {
            return false;
        }

        string trimmedCode = code.Trim();
        if (trimmedCode.Length != CodeDigits || !trimmedCode.All(char.IsDigit))
        {
            return false;
        }

        byte[] key = FromBase32(base32Secret);
        long currentCounter = timestamp.ToUnixTimeSeconds() / TimeStepSeconds;

        for (int offset = -windowSteps; offset <= windowSteps; offset++)
        {
            string candidate = ComputeHotp(key, currentCounter + offset);
            if (CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(candidate),
                Encoding.ASCII.GetBytes(trimmedCode)))
            {
                return true;
            }
        }

        return false;
    }

    public IReadOnlyList<string> GenerateRecoveryCodes(int count = 10)
    {
        var codes = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            byte[] randomBytes = RandomNumberGenerator.GetBytes(5);
            string hex = Convert.ToHexString(randomBytes);
            codes.Add($"{hex[..5]}-{hex[5..10]}");
        }

        return codes.AsReadOnly();
    }

    public string HashRecoveryCode(string rawCode)
    {
        string normalized = rawCode.Trim().ToUpperInvariant().Replace("-", string.Empty);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes($"mw-recovery-v1:{normalized}"));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string ComputeHotp(byte[] key, long counter)
    {
        Span<byte> counterBytes = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counterBytes, counter);

        byte[] hash = HMACSHA1.HashData(key, counterBytes);
        int offset = hash[^1] & 0x0F;

        int binaryCode =
            ((hash[offset] & 0x7F) << 24) |
            ((hash[offset + 1] & 0xFF) << 16) |
            ((hash[offset + 2] & 0xFF) << 8) |
            (hash[offset + 3] & 0xFF);

        int otp = binaryCode % 1_000_000;
        return otp.ToString("D6");
    }

    public static string ToBase32(ReadOnlySpan<byte> data)
    {
        if (data.IsEmpty)
        {
            return string.Empty;
        }

        var result = new StringBuilder((data.Length * 8 + 4) / 5);
        int buffer = data[0];
        int next = 1;
        int bitsLeft = 8;

        while (bitsLeft > 0 || next < data.Length)
        {
            if (bitsLeft < 5)
            {
                if (next < data.Length)
                {
                    buffer <<= 8;
                    buffer |= data[next++] & 0xFF;
                    bitsLeft += 8;
                }
                else
                {
                    int pad = 5 - bitsLeft;
                    buffer <<= pad;
                    bitsLeft += pad;
                }
            }

            int index = 0x1F & (buffer >> (bitsLeft - 5));
            bitsLeft -= 5;
            result.Append(Base32Alphabet[index]);
        }

        return result.ToString();
    }

    public static byte[] FromBase32(string base32)
    {
        if (string.IsNullOrWhiteSpace(base32))
        {
            return [];
        }

        string cleaned = base32.Trim().TrimEnd('=').ToUpperInvariant();
        var bytes = new List<byte>(cleaned.Length * 5 / 8);

        int buffer = 0;
        int bitsLeft = 0;

        foreach (char c in cleaned)
        {
            int val = Base32Alphabet.IndexOf(c);
            if (val < 0)
            {
                continue;
            }

            buffer = (buffer << 5) | val;
            bitsLeft += 5;

            if (bitsLeft >= 8)
            {
                bytes.Add((byte)((buffer >> (bitsLeft - 8)) & 0xFF));
                bitsLeft -= 8;
            }
        }

        return [.. bytes];
    }
}
