using System.Buffers.Text;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Infrastructure.Identity;

/// <summary>The RSA public keys Google signs ID tokens with, by key id.</summary>
public interface IGoogleSigningKeys
{
    /// <param name="refresh">Ask the source for fresh keys, because a token named a key id the cached set does not hold.</param>
    ValueTask<IReadOnlyDictionary<string, RSAParameters>> GetAsync(bool refresh, CancellationToken cancellationToken);
}

/// <summary>
/// Verifies a Google ID token (OpenID Connect): RS256 signature by a published Google key, issuer, audience equal to this
/// application's client id, lifetime, and a verified e-mail. Anything else is refused; there is no unsigned or test path.
/// </summary>
public sealed class GoogleIdTokenVerifier(string clientId, IGoogleSigningKeys keys) : IGoogleIdTokenVerifier
{
    public const int MaxTokenLength = 8 * 1024;
    private static readonly TimeSpan Skew = TimeSpan.FromSeconds(60);

    public async Task<GoogleIdentity> VerifyAsync(string idToken, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw new InvalidOperationException("Google sign-in has no client id configured.");
        if (string.IsNullOrWhiteSpace(idToken) || idToken.Length > MaxTokenLength)
            throw Invalid("A Google ID token (at most 8 KB) is required.");

        string[] parts = idToken.Trim().Split('.');
        if (parts.Length != 3)
            throw Invalid("The Google ID token is malformed.");

        using JsonDocument header = ParseSegment(parts[0]);
        if (Text(header.RootElement, "alg") != "RS256")
            throw Invalid("The Google ID token must be signed with RS256.");
        string kid = Text(header.RootElement, "kid") ?? throw Invalid("The Google ID token names no signing key.");

        byte[] signature = Decode(parts[2]);
        byte[] signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        if (!await SignatureMatchesAsync(kid, signed, signature, cancellationToken))
            throw Invalid("The Google ID token signature is not valid.");

        // Claims are read only after the signature holds, so nothing below can be steered by a forged token.
        using JsonDocument payload = ParseSegment(parts[1]);
        JsonElement claims = payload.RootElement;

        if (Text(claims, "iss") is not ("https://accounts.google.com" or "accounts.google.com"))
            throw Invalid("The Google ID token has the wrong issuer.");
        if (!claims.TryGetProperty("aud", out JsonElement aud) || aud.ValueKind != JsonValueKind.String || aud.GetString() != clientId)
            throw Invalid("The Google ID token was issued for a different application.");
        if (!claims.TryGetProperty("exp", out JsonElement exp) || exp.ValueKind != JsonValueKind.Number || !exp.TryGetInt64(out long expUnix))
            throw Invalid("The Google ID token has no expiry.");
        if (DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime + Skew <= utcNow)
            throw new UnauthorizedAccessException("The Google ID token has expired.");
        if (claims.TryGetProperty("iat", out JsonElement iat) && iat.ValueKind == JsonValueKind.Number && iat.TryGetInt64(out long iatUnix)
            && DateTimeOffset.FromUnixTimeSeconds(iatUnix).UtcDateTime - Skew > utcNow)
            throw Invalid("The Google ID token was issued in the future.");

        // Only an e-mail Google vouches for may find or create an account; a missing claim is not a yes.
        if (!claims.TryGetProperty("email_verified", out JsonElement verified) || verified.ValueKind != JsonValueKind.True)
            throw Invalid("Google has not verified this account's e-mail.");

        string subject = Text(claims, "sub") is { Length: > 0 } sub ? sub : throw Invalid("The Google ID token has no subject.");
        string email = Text(claims, "email") is { Length: > 0 } mail ? mail.Trim().ToLowerInvariant() : throw Invalid("The Google ID token has no e-mail.");
        string name = Text(claims, "name") is { Length: > 0 } n ? n.Trim() : email;
        return new GoogleIdentity(subject, email, name);
    }

    private async Task<bool> SignatureMatchesAsync(string kid, byte[] signed, byte[] signature, CancellationToken ct)
    {
        IReadOnlyDictionary<string, RSAParameters> known = await keys.GetAsync(refresh: false, ct);
        if (!known.TryGetValue(kid, out RSAParameters key))
        {
            // Google rotates keys; an unknown id is worth one refresh, never more.
            known = await keys.GetAsync(refresh: true, ct);
            if (!known.TryGetValue(kid, out key)) return false;
        }

        using RSA rsa = RSA.Create();
        rsa.ImportParameters(key);
        return rsa.VerifyData(signed, signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
    }

    private static JsonDocument ParseSegment(string segment)
    {
        try { return JsonDocument.Parse(Decode(segment)); }
        catch (JsonException) { throw Invalid("The Google ID token is malformed."); }
    }

    internal static byte[] Decode(string segment)
    {
        try { return Base64Url.DecodeFromChars(segment); }
        catch (FormatException) { throw Invalid("The Google ID token is malformed."); }
    }

    private static string? Text(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static ValidationException Invalid(string message) => new("idToken", message);
}

/// <summary>
/// Google's published JWKS, cached for as long as Google's Cache-Control allows. A refresh asked for because of an unknown key
/// id happens at most once a minute, so forged key ids cannot turn into a flood of requests to Google.
/// </summary>
public sealed class GoogleJwksSigningKeys(Func<HttpClient> httpClient, TimeProvider clock) : IGoogleSigningKeys
{
    public const string HttpClientName = "GoogleJwks";
    public const string JwksUrl = "https://www.googleapis.com/oauth2/v3/certs";
    private static readonly TimeSpan MinRefreshInterval = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan DefaultLifetime = TimeSpan.FromHours(1);

    private static readonly TimeSpan FailureBackoff = TimeSpan.FromSeconds(10);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private Exception? _lastFailure;
    private IReadOnlyDictionary<string, RSAParameters> _keys = new Dictionary<string, RSAParameters>();
    private DateTimeOffset _expiresAt = DateTimeOffset.MinValue;
    private DateTimeOffset _fetchedAt = DateTimeOffset.MinValue;

    public async ValueTask<IReadOnlyDictionary<string, RSAParameters>> GetAsync(bool refresh, CancellationToken cancellationToken)
    {
        if (!NeedsFetch(refresh)) return KeysOrFailure();

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!NeedsFetch(refresh)) return KeysOrFailure();
            DateTimeOffset now = clock.GetUtcNow();
            _fetchedAt = now;

            try
            {
                using HttpResponseMessage response = await httpClient().GetAsync(JwksUrl, cancellationToken);
                response.EnsureSuccessStatusCode();
                await using Stream body = await response.Content.ReadAsStreamAsync(cancellationToken);
                var fresh = ParseJwks(await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken));
                if (fresh.Count == 0) throw new InvalidDataException("Google's key set holds no RSA keys.");
                _keys = fresh;
                _lastFailure = null;
                _expiresAt = now + (MaxAge(response.Headers.CacheControl) ?? DefaultLifetime);
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                // Google keeps a retired key published for days, so the last good set keeps working through a short outage.
                // With nothing cached there is nothing to check a signature against: sign-in fails closed, and for a short
                // back-off every caller gets that answer at once instead of queueing for its own slow fetch.
                _lastFailure = new InvalidOperationException("Google's signing keys could not be loaded.", ex);
                _expiresAt = now + (_keys.Count == 0 ? FailureBackoff : MinRefreshInterval);
            }
            return KeysOrFailure();
        }
        finally
        {
            _gate.Release();
        }
    }

    private IReadOnlyDictionary<string, RSAParameters> KeysOrFailure() =>
        _keys.Count == 0 && _lastFailure is not null ? throw _lastFailure : _keys;

    private bool NeedsFetch(bool refresh)
    {
        DateTimeOffset now = clock.GetUtcNow();
        return now >= _expiresAt || (refresh && now - _fetchedAt >= MinRefreshInterval);
    }

    private static TimeSpan? MaxAge(CacheControlHeaderValue? cache) =>
        cache?.MaxAge is { } age && age > TimeSpan.Zero ? (age > TimeSpan.FromDays(1) ? TimeSpan.FromDays(1) : age) : null;

    public static IReadOnlyDictionary<string, RSAParameters> ParseJwks(JsonDocument jwks)
    {
        using (jwks)
        {
            var keys = new Dictionary<string, RSAParameters>(StringComparer.Ordinal);
            if (!jwks.RootElement.TryGetProperty("keys", out JsonElement list) || list.ValueKind != JsonValueKind.Array) return keys;
            foreach (JsonElement k in list.EnumerateArray())
            {
                // One odd entry must not cost the good keys next to it, so each is read on its own and skipped if unusable.
                if (Str(k, "kty") == "RSA" && Str(k, "kid") is { Length: > 0 } id && Str(k, "n") is { Length: > 0 } n && Str(k, "e") is { Length: > 0 } e
                    && TryDecode(n, out byte[] modulus) && TryDecode(e, out byte[] exponent))
                {
                    keys[id] = new RSAParameters { Modulus = modulus, Exponent = exponent };
                }
            }
            return keys;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryDecode(string value, out byte[] bytes)
    {
        try { bytes = Base64Url.DecodeFromChars(value); return bytes.Length > 0; }
        catch (FormatException) { bytes = []; return false; }
    }
}
