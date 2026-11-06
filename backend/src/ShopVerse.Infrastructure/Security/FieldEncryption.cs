using System.Security.Cryptography;
using System.Text;

namespace ShopVerse.Infrastructure.Security;

/// <summary>
/// Application-level encryption of identifying business numbers (threat model v1 H4, brief §11.1 "KYC numbers encrypted at
/// application level"). A database dump or a read-only replica shows ciphertext; only the API, holding the key, reads them.
/// <list type="bullet">
/// <item>Values: AES-256-GCM with a random nonce, stored as <c>enc:v2:&lt;key id&gt;:</c> + base64(nonce | ciphertext | tag).
/// The same BIN never encrypts the same way twice, and a changed byte fails to decrypt instead of reading as something else.
/// The first release wrote <c>enc:v1:</c> without a key id; those values are still read and are rewritten by the backfill.</item>
/// <item>Lookups: a blind index, HMAC-SHA256 under a second key, so uniqueness and "is this BIN taken" work without
/// decrypting anything.</item>
/// <item>Rotation: a new master key encrypts; the previous ones (<c>Encryption:PreviousFieldKeys</c>) only read, until the
/// backfill has rewritten every value and index under the new key and they can be retired.</item>
/// </list>
/// Both working keys are derived (HKDF) from one 32-byte master key.
/// </summary>
public sealed class FieldProtector
{
    /// <summary>What every protected value starts with, whatever the format or key.</summary>
    public const string Prefix = "enc:";
    private const string V1Prefix = "enc:v1:";
    private const int NonceSize = 12;
    private const int TagSize = 16;

    private sealed record Key(string Id, byte[] Encryption, byte[] Index);

    private readonly Key _current;
    private readonly Key[] _all;

    public FieldProtector(byte[] masterKey, IEnumerable<byte[]>? previousKeys = null)
    {
        _current = Derive(masterKey);
        _all = [_current, .. (previousKeys ?? []).Select(Derive).Where(k => k.Id != _current.Id).DistinctBy(k => k.Id)];
        CurrentPrefix = $"enc:v2:{_current.Id}:";
    }

    private static Key Derive(byte[] master)
    {
        if (master is not { Length: 32 }) throw new ArgumentException("A field encryption key must be 32 bytes.", nameof(master));
        byte[] Hkdf(string info, int length) => HKDF.DeriveKey(HashAlgorithmName.SHA256, master, length, info: Encoding.UTF8.GetBytes(info));
        return new Key(Convert.ToHexStringLower(Hkdf("mechware/key-id/v1", 4)),
            Hkdf("mechware/field-encryption/v1", 32), Hkdf("mechware/blind-index/v1", 32));
    }

    /// <summary>The prefix of a value written under the current key; anything else is rewritten by the backfill.</summary>
    public string CurrentPrefix { get; }

    public static bool IsProtected(string? stored) => stored is not null && stored.StartsWith(Prefix, StringComparison.Ordinal);

    public bool IsCurrent(string? stored) => stored is not null && stored.StartsWith(CurrentPrefix, StringComparison.Ordinal);

    /// <summary>Always encrypts: the value comes from the model and is plain, even when it happens to look like ciphertext
    /// (a user can type "enc:..." as a licence number).</summary>
    public string Protect(string plain)
    {
        byte[] data = Encoding.UTF8.GetBytes(plain);
        byte[] output = new byte[NonceSize + data.Length + TagSize];
        Span<byte> nonce = output.AsSpan(0, NonceSize);
        RandomNumberGenerator.Fill(nonce);
        using var aes = new AesGcm(_current.Encryption, TagSize);
        aes.Encrypt(nonce, data, output.AsSpan(NonceSize, data.Length), output.AsSpan(NonceSize + data.Length, TagSize));
        return CurrentPrefix + Convert.ToBase64String(output);
    }

    /// <summary>Decrypts; a value written before encryption was introduced (no prefix) is returned as it is.</summary>
    public string Unprotect(string stored)
    {
        if (!IsProtected(stored)) return stored;
        if (stored.StartsWith(V1Prefix, StringComparison.Ordinal))
        {
            // No key id: the GCM tag tells which configured key wrote it.
            byte[] v1 = Convert.FromBase64String(stored[V1Prefix.Length..]);
            foreach (var key in _all)
            {
                try { return Decrypt(key, v1); }
                catch (AuthenticationTagMismatchException) { }
            }
            throw new CryptographicException("The protected value was written with a key that is not configured.");
        }

        string[] parts = stored.Split(':', 4);
        if (parts is not ["enc", "v2", var id, var body]) throw new CryptographicException("Unknown protected value format.");
        var match = _all.FirstOrDefault(k => k.Id == id)
                    ?? throw new CryptographicException("The protected value was written with a key that is not configured.");
        return Decrypt(match, Convert.FromBase64String(body));
    }

    private static string Decrypt(Key key, byte[] input)
    {
        if (input.Length < NonceSize + TagSize) throw new CryptographicException("The protected value is truncated.");
        int length = input.Length - NonceSize - TagSize;
        byte[] plain = new byte[length];
        using var aes = new AesGcm(key.Encryption, TagSize);
        aes.Decrypt(input.AsSpan(0, NonceSize), input.AsSpan(NonceSize, length), input.AsSpan(NonceSize + length, TagSize), plain);
        return Encoding.UTF8.GetString(plain);
    }

    /// <summary>Deterministic, keyed fingerprint under the current key; whitespace and letter case do not matter.</summary>
    public string BlindIndex(string value) => Index(_current, value);

    /// <summary>The fingerprint under every configured key, current first: lookups during a rotation must match rows not yet
    /// rewritten.</summary>
    public IReadOnlyList<string> BlindIndexes(string value) => [.. _all.Select(k => Index(k, value))];

    private static string Index(Key key, string value) =>
        Convert.ToHexStringLower(HMACSHA256.HashData(key.Index, Encoding.UTF8.GetBytes(value.Trim().ToUpperInvariant())));
}

/// <summary>
/// The protector the EF value converters use. Configured once at start-up from <c>Encryption:FieldKey</c>; development and
/// tests use a fixed, published key, which the API refuses outside Development.
/// </summary>
public static class FieldEncryption
{
    public static readonly byte[] DevelopmentKey = SHA256.HashData(Encoding.UTF8.GetBytes("mechware-development-field-key-not-for-production"));

    private static string _configured = Fingerprint(DevelopmentKey, []);

    public static FieldProtector Current { get; private set; } = new(DevelopmentKey);

    /// <summary>Sets the process-wide keys; setting the keys already in use changes nothing.</summary>
    public static void Configure(byte[] masterKey, IReadOnlyList<byte[]>? previousKeys = null)
    {
        previousKeys ??= [];
        string fingerprint = Fingerprint(masterKey, previousKeys);
        if (fingerprint == _configured) return;
        Current = new FieldProtector(masterKey, previousKeys);
        _configured = fingerprint;
    }

    private static string Fingerprint(byte[] masterKey, IReadOnlyList<byte[]> previousKeys) =>
        Convert.ToHexString(SHA256.HashData([.. masterKey, .. previousKeys.SelectMany(k => k)]));

    /// <summary>
    /// Configures the keys from configuration values. Outside Development (<paramref name="requireRealKey"/>) a key must be set
    /// and must not be the published development key; in Development a missing key keeps the development key.
    /// </summary>
    public static void ConfigureFrom(string? fieldKeyBase64, string? previousKeysBase64, bool requireRealKey)
    {
        byte[]? fieldKey = ParseKey(fieldKeyBase64);
        if (fieldKey is null && requireRealKey)
            throw new InvalidOperationException("Encryption:FieldKey is not set; set Encryption__FieldKey (32 bytes, base64) from the secret store.");
        if (fieldKey is not null && requireRealKey && fieldKey.AsSpan().SequenceEqual(DevelopmentKey))
            throw new InvalidOperationException("Encryption:FieldKey is the published development key; set a real one from the secret store.");
        var previousKeys = ParseKeys(previousKeysBase64);
        if (fieldKey is not null) Configure(fieldKey, previousKeys);
    }

    /// <summary>Reads a comma-separated list of base64 32-byte keys; empty when none is configured.</summary>
    public static IReadOnlyList<byte[]> ParseKeys(string? base64List) =>
        string.IsNullOrWhiteSpace(base64List)
            ? []
            : [.. base64List.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => ParseKey(k)!)];

    /// <summary>Reads a base64 32-byte key; null when none is configured.</summary>
    public static byte[]? ParseKey(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        byte[] key = Convert.FromBase64String(base64.Trim());
        return key.Length == 32 ? key : throw new InvalidOperationException("Encryption:FieldKey must be 32 bytes, base64-encoded.");
    }
}
