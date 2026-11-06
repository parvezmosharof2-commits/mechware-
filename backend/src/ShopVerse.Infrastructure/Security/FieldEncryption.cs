using System.Security.Cryptography;
using System.Text;

namespace ShopVerse.Infrastructure.Security;

/// <summary>
/// Application-level encryption of identifying business numbers (threat model v1 H4, brief §11.1 "KYC numbers encrypted at
/// application level"). A database dump or a read-only replica shows ciphertext; only the API, holding the key, reads them.
/// <list type="bullet">
/// <item>Values: AES-256-GCM with a random nonce, stored as <c>enc:v1:</c> + base64(nonce | ciphertext | tag). The same BIN
/// never encrypts the same way twice, and a changed byte fails to decrypt instead of reading as something else.</item>
/// <item>Lookups: a blind index, HMAC-SHA256 under a second key, so uniqueness and "is this BIN taken" work without
/// decrypting anything.</item>
/// </list>
/// Both keys are derived (HKDF) from one 32-byte master key from configuration (<c>Encryption:FieldKey</c>).
/// </summary>
public sealed class FieldProtector
{
    public const string Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private readonly byte[] _encryptionKey;
    private readonly byte[] _indexKey;

    public FieldProtector(byte[] masterKey)
    {
        if (masterKey is not { Length: 32 }) throw new ArgumentException("The field encryption key must be 32 bytes.", nameof(masterKey));
        _encryptionKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.UTF8.GetBytes("mechware/field-encryption/v1"));
        _indexKey = HKDF.DeriveKey(HashAlgorithmName.SHA256, masterKey, 32, info: Encoding.UTF8.GetBytes("mechware/blind-index/v1"));
    }

    public static bool IsProtected(string? stored) => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public string Protect(string plain)
    {
        if (IsProtected(plain)) return plain;
        byte[] data = Encoding.UTF8.GetBytes(plain);
        byte[] output = new byte[NonceSize + data.Length + TagSize];
        Span<byte> nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize, data.Length), output.AsSpan(NonceSize + data.Length, TagSize));
        return Prefix + Convert.ToBase64String(output);
    }

    /// <summary>Decrypts; a value written before encryption was introduced (no prefix) is returned as it is.</summary>
    public string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;
        byte[] input = Convert.FromBase64String(stored[Prefix.Length..]);
        if (input.Length < NonceSize + TagSize) throw new CryptographicException("The protected value is truncated.");
        int length = input.Length - NonceSize - TagSize;
        byte[] plain = new byte[length];
        using var aes = new AesGcm(_encryptionKey, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, length), input.AsSpan(NonceSize + length, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Deterministic, keyed fingerprint for equality lookups; whitespace and letter case do not matter.</summary>
    public string BlindIndex(string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(_indexKey, Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())));
}

/// <summary>
/// The protector the EF value converters use. Configured once at start-up from <c>Encryption:FieldKey</c>; development and
/// tests use a fixed, published key, which the API refuses outside Development.
/// </summary>
public static class FieldEncryption
{
    public static readonly byte[] DevelopmentKey = SHA256.HashData(Encoding.UTF8.GetBytes("mechware-development-field-key-not-for-production"));

    private static byte[] _currentKey = DevelopmentKey;

    public static FieldProtector Current { get; private set; } = new(DevelopmentKey);

    /// <summary>Sets the process-wide key; setting the key already in use changes nothing.</summary>
    public static void Configure(byte[] masterKey)
    {
        if (masterKey.AsSpan().SequenceEqual(_currentKey)) return;
        Current = new FieldProtector(masterKey);
        _currentKey = masterKey.ToArray();
    }

    /// <summary>Reads a base64 32-byte key; null when none is configured.</summary>
    public static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        byte[] key = Convert.FromBase64String(base64.Trim());
        return key.Length == 32 ? key : throw new InvalidOperationException("Encryption:FieldKey must be 32 bytes, base64-encoded.");
    }
}
