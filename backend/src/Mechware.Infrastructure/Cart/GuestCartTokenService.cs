using System.Buffers;
using System.Buffers.Binary;
using System.Buffers.Text;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Configuration;
using Mechware.Application.Cart;

namespace Mechware.Infrastructure.Cart;

/// <summary>
/// Keys of the guest-cart token (configuration section <c>GuestCart</c>). <c>TokenKey</c> signs new tokens;
/// <c>PreviousTokenKeys</c> (comma-separated) only verify, so tokens signed before a key rotation keep working until they
/// expire (at most <see cref="ICartStore.GuestTtl"/>), after which the old key can be removed.
/// </summary>
public sealed class GuestCartTokenOptions
{
    public const string SectionName = "GuestCart";
    public const int MinKeyBytes = 32;

    /// <summary>Published in this repository: used only in Development and Testing when no key is configured.</summary>
    public static readonly byte[] DevelopmentKey = SHA256.HashData(Encoding.UTF8.GetBytes("mechware-development-guest-cart-token-key-not-for-production"));

    /// <summary>Base64 of at least <see cref="MinKeyBytes"/> random bytes (<c>openssl rand -base64 32</c>).</summary>
    public string? TokenKey { get; init; }

    public string? PreviousTokenKeys { get; init; }

    public static GuestCartTokenOptions From(IConfiguration configuration) => new()
    {
        TokenKey = configuration[$"{SectionName}:TokenKey"],
        PreviousTokenKeys = configuration[$"{SectionName}:PreviousTokenKeys"],
    };

    /// <summary>
    /// The signing key and the verify-only keys. Throws (so the host refuses to start) on a malformed or short key, and
    /// outside Development/Testing (<paramref name="requireRealKey"/>) on a missing key or the published development key,
    /// also among the previous keys: one published key there would let anyone mint tokens.
    /// </summary>
    public (byte[] Current, byte[][] Previous) ResolveKeys(bool requireRealKey)
    {
        byte[]? current = Parse(TokenKey);
        if (current is null && requireRealKey)
            throw new InvalidOperationException("GuestCart:TokenKey is not set; set GuestCart__TokenKey (at least 32 random bytes, base64) from the secret store.");
        byte[][] previous = string.IsNullOrWhiteSpace(PreviousTokenKeys)
            ? []
            : [.. PreviousTokenKeys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(k => Parse(k)!)];
        if (requireRealKey && previous.Prepend(current!).Any(k => k.AsSpan().SequenceEqual(DevelopmentKey)))
            throw new InvalidOperationException("GuestCart:TokenKey is the published development key; set a real one from the secret store.");
        return (current ?? DevelopmentKey, previous);
    }

    private static byte[]? Parse(string? base64)
    {
        if (string.IsNullOrWhiteSpace(base64)) return null;
        byte[] key;
        try { key = Convert.FromBase64String(base64.Trim()); }
        catch (FormatException) { throw new InvalidOperationException("GuestCart:TokenKey (and each previous key) must be base64."); }
        if (key.Length < MinKeyBytes || key.All(b => b == key[0]))
            throw new InvalidOperationException($"GuestCart:TokenKey (and each previous key) must be at least {MinKeyBytes} random bytes.");
        return key;
    }
}

/// <summary>
/// <see cref="IGuestCartTokenService"/> as a stateless HMAC token:
/// <c>base64url(guest id 16 | expiry unix seconds 8 | version 1) "." base64url(HMAC-SHA256(key, payload))</c>, 78 characters.
/// <list type="bullet">
/// <item>The guest id is a random v4 GUID (122 random bits) minted by the server, so it cannot be guessed; the token adds
/// <b>integrity</b> (a forged or altered id or expiry is refused), not secrecy: the payload is readable by whoever holds the
/// cookie, and nothing in it is confidential.</item>
/// <item>The expiry is inside the signed payload (lifetime <see cref="ICartStore.GuestTtl"/>); sliding is done by the cart
/// endpoints re-issuing a fresh token on each response (E1.3).</item>
/// <item>Keys: HKDF-derived from the configured secret (domain separated), current key signs, previous keys only verify.</item>
/// <item><see cref="Validate"/> checks the exact length and canonical encoding before decoding, compares the MAC in constant
/// time against every key before reading any field, never throws, and returns null without saying why.</item>
/// </list>
/// </summary>
public sealed class GuestCartTokenService : IGuestCartTokenService
{
    public const byte Version = 1;
    private const int IdBytes = 16, ExpiryBytes = 8, PayloadBytes = IdBytes + ExpiryBytes + 1, MacBytes = 32;
    private static readonly int PayloadChars = Base64Url.GetEncodedLength(PayloadBytes);
    public static readonly int TokenLength = PayloadChars + 1 + Base64Url.GetEncodedLength(MacBytes);

    /// <summary>A token claiming to live longer than any token this service issues is refused (clock skew allowed).</summary>
    private static readonly TimeSpan MaxClockSkew = TimeSpan.FromMinutes(5);

    private readonly byte[] _signingKey;
    private readonly byte[][] _verifyingKeys;
    private readonly TimeProvider _clock;

    public GuestCartTokenService(byte[] tokenKey, IEnumerable<byte[]>? previousTokenKeys, TimeProvider clock)
    {
        _signingKey = DeriveKey(tokenKey);
        _verifyingKeys = [_signingKey, .. (previousTokenKeys ?? []).Select(DeriveKey)];
        _clock = clock;
    }

    internal static byte[] DeriveKey(byte[] secret) =>
        HKDF.DeriveKey(HashAlgorithmName.SHA256, secret, 32, info: Encoding.UTF8.GetBytes("mechware/guest-cart-token/v1"));

    public string Issue(GuestCartId guestCartId)
    {
        if (guestCartId.Value == Guid.Empty) throw new ArgumentException("A guest cart id is required.", nameof(guestCartId));
        Span<byte> payload = stackalloc byte[PayloadBytes];
        guestCartId.Value.TryWriteBytes(payload[..IdBytes], bigEndian: true, out _);
        BinaryPrimitives.WriteInt64BigEndian(payload.Slice(IdBytes, ExpiryBytes), (_clock.GetUtcNow() + ICartStore.GuestTtl).ToUnixTimeSeconds());
        payload[^1] = Version;
        Span<byte> mac = stackalloc byte[MacBytes];
        HMACSHA256.HashData(_signingKey, payload, mac);
        return Base64Url.EncodeToString(payload) + "." + Base64Url.EncodeToString(mac);
    }

    public GuestCartId? Validate(string? token)
    {
        // The length is fixed, so anything else (including a huge string) is refused before any decoding.
        if (token is null || token.Length != TokenLength || token[PayloadChars] != '.') return null;
        Span<byte> payload = stackalloc byte[PayloadBytes];
        Span<byte> mac = stackalloc byte[MacBytes];
        if (!TryDecodeCanonical(token.AsSpan(0, PayloadChars), payload) || !TryDecodeCanonical(token.AsSpan(PayloadChars + 1), mac)) return null;

        // Every key is tried (no early exit), each compare in constant time.
        Span<byte> expected = stackalloc byte[MacBytes];
        bool authentic = false;
        foreach (byte[] key in _verifyingKeys)
        {
            HMACSHA256.HashData(key, payload, expected);
            authentic |= CryptographicOperations.FixedTimeEquals(expected, mac);
        }
        if (!authentic) return null;

        if (payload[^1] != Version) return null;
        long expiry = BinaryPrimitives.ReadInt64BigEndian(payload.Slice(IdBytes, ExpiryBytes));
        long now = _clock.GetUtcNow().ToUnixTimeSeconds();
        if (expiry <= now || expiry - now > (long)(ICartStore.GuestTtl + MaxClockSkew).TotalSeconds) return null;
        var id = new Guid(payload[..IdBytes], bigEndian: true);
        return id == Guid.Empty ? null : new GuestCartId(id);
    }

    /// <summary>
    /// Decodes exactly <paramref name="destination"/>.Length bytes, and only from their one canonical spelling (no padding,
    /// whitespace or stray low bits), so a token has exactly one valid string form and a changed character never passes.
    /// </summary>
    private static bool TryDecodeCanonical(ReadOnlySpan<char> text, Span<byte> destination)
    {
        if (Base64Url.DecodeFromChars(text, destination, out int consumed, out int written) != OperationStatus.Done
            || consumed != text.Length || written != destination.Length)
            return false;
        Span<char> canonical = stackalloc char[Base64Url.GetEncodedLength(destination.Length)];
        return Base64Url.TryEncodeToChars(destination, canonical, out int chars) && canonical[..chars].SequenceEqual(text);
    }
}
