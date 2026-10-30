using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// RFC 7519 JWT token generator/validator and high-entropy refresh token hashing service.
/// Uses HMAC-SHA256 with constant-time signature verification and zero external package dependencies.
/// </summary>
public sealed class JwtTokenService : ITokenService
{
    public const string PermissionClaimType = "permission";
    public const string SellerIdClaimType = "seller_id";

    /// <summary>Tokens longer than this are rejected before any decoding (resource-exhaustion guard).</summary>
    public const int MaxTokenLength = 8192;

    private const long ClockSkewSeconds = 60;

    private readonly byte[] _signingKey;
    private readonly string _issuer;
    private readonly string _audience;
    private readonly int _accessTokenExpiryMinutes;

    public JwtTokenService(
        string secret,
        string issuer = "https://api.mechware.com.bd",
        string audience = "https://mechware.com.bd",
        int accessTokenExpiryMinutes = 15)
    {
        if (string.IsNullOrWhiteSpace(secret) || secret.Length < 32)
        {
            throw new ArgumentException("JWT signing secret must be at least 32 characters.", nameof(secret));
        }

        _signingKey = Encoding.UTF8.GetBytes(secret);
        _issuer = issuer;
        _audience = audience;
        _accessTokenExpiryMinutes = accessTokenExpiryMinutes;
    }

    public string GenerateAccessToken(
        User user,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> permissions,
        DateTime utcNow)
    {
        long iat = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
        long exp = new DateTimeOffset(DateTime.SpecifyKind(utcNow.AddMinutes(_accessTokenExpiryMinutes), DateTimeKind.Utc)).ToUnixTimeSeconds();

        var headerJson = JsonSerializer.Serialize(new
        {
            alg = "HS256",
            typ = "JWT"
        });

        var payloadDict = new Dictionary<string, object?>
        {
            ["sub"] = user.Id.ToString(),
            ["email"] = user.Email,
            ["name"] = user.FullName,
            ["locale"] = user.Locale,
            ["jti"] = Guid.NewGuid().ToString("N"),
            ["iss"] = _issuer,
            ["aud"] = _audience,
            ["iat"] = iat,
            ["exp"] = exp,
            ["roles"] = roles,
            [PermissionClaimType] = permissions
        };

        if (user.SellerId.HasValue)
        {
            payloadDict[SellerIdClaimType] = user.SellerId.Value.ToString();
        }

        string payloadJson = JsonSerializer.Serialize(payloadDict);

        string encodedHeader = Base64UrlEncode(Encoding.UTF8.GetBytes(headerJson));
        string encodedPayload = Base64UrlEncode(Encoding.UTF8.GetBytes(payloadJson));
        string signingInput = $"{encodedHeader}.{encodedPayload}";

        byte[] signatureBytes = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(signingInput));
        string encodedSignature = Base64UrlEncode(signatureBytes);

        return $"{signingInput}.{encodedSignature}";
    }

    public ClaimsPrincipal? ValidateAccessToken(string token, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(token) || token.Length > MaxTokenLength)
        {
            return null;
        }

        string[] parts = token.Trim().Split('.');
        if (parts.Length != 3 || parts.Any(p => p.Length == 0))
        {
            return null;
        }

        byte[] headerBytes;
        byte[] payloadBytes;
        byte[] providedSignature;
        try
        {
            headerBytes = Base64UrlDecodeCanonical(parts[0]);
            payloadBytes = Base64UrlDecodeCanonical(parts[1]);
            providedSignature = Base64UrlDecodeCanonical(parts[2]);
        }
        catch (FormatException)
        {
            return null;
        }

        string signingInput = $"{parts[0]}.{parts[1]}";
        byte[] expectedSignature = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(signingInput));
        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
        {
            return null;
        }

        // Even with a valid MAC, the header must name exactly the algorithm we issue (no alg confusion,
        // no critical extensions we do not understand) and the payload must be well-formed.
        try
        {
            return IsAcceptedHeader(headerBytes) ? BuildPrincipal(payloadBytes, utcNow) : null;
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or ArgumentException or FormatException)
        {
            return null;
        }
    }

    private static bool IsAcceptedHeader(byte[] headerBytes)
    {
        using var doc = JsonDocument.Parse(headerBytes);
        JsonElement header = doc.RootElement;
        if (header.ValueKind != JsonValueKind.Object) return false;
        if (!header.TryGetProperty("alg", out JsonElement alg) || alg.ValueKind != JsonValueKind.String || alg.GetString() != "HS256") return false;
        if (header.TryGetProperty("typ", out JsonElement typ) && (typ.ValueKind != JsonValueKind.String || typ.GetString() != "JWT")) return false;
        return !header.TryGetProperty("crit", out _);
    }

    private ClaimsPrincipal? BuildPrincipal(byte[] payloadBytes, DateTime utcNow)
    {
        using var doc = JsonDocument.Parse(payloadBytes);
        JsonElement root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return null;

        long nowUnix = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();

        if (!root.TryGetProperty("exp", out JsonElement expEl) || expEl.ValueKind != JsonValueKind.Number || !expEl.TryGetInt64(out long expUnix) || nowUnix >= expUnix)
        {
            return null;
        }

        if (root.TryGetProperty("nbf", out JsonElement nbfEl) && (!nbfEl.TryGetInt64(out long nbf) || nowUnix + ClockSkewSeconds < nbf))
        {
            return null;
        }

        if (root.TryGetProperty("iat", out JsonElement iatEl) && (!iatEl.TryGetInt64(out long iat) || iat > nowUnix + ClockSkewSeconds))
        {
            return null;
        }

        if (StringClaim(root, "iss") != _issuer || StringClaim(root, "aud") != _audience)
        {
            return null;
        }

        var claims = new List<Claim>();

        if (StringClaim(root, "sub") is string sub)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
            claims.Add(new Claim("sub", sub));
        }

        if (StringClaim(root, "email") is string email)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        if (StringClaim(root, "name") is string name)
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }

        if (StringClaim(root, SellerIdClaimType) is string sellerId)
        {
            claims.Add(new Claim(SellerIdClaimType, sellerId));
        }

        foreach (string role in StringArrayClaim(root, "roles"))
        {
            claims.Add(new Claim(ClaimTypes.Role, role));
        }

        foreach (string perm in StringArrayClaim(root, PermissionClaimType))
        {
            claims.Add(new Claim(PermissionClaimType, perm));
        }

        var identity = new ClaimsIdentity(claims, "Bearer");
        return new ClaimsPrincipal(identity);
    }

    private static string? StringClaim(JsonElement root, string name) =>
        root.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String ? el.GetString() : null;

    private static IEnumerable<string> StringArrayClaim(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out JsonElement el) || el.ValueKind != JsonValueKind.Array)
        {
            yield break;
        }

        foreach (JsonElement item in el.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String && item.GetString() is string value && !string.IsNullOrWhiteSpace(value))
            {
                yield return value;
            }
        }
    }

    public string GenerateRawRefreshToken()
    {
        byte[] randomBytes = RandomNumberGenerator.GetBytes(32);
        return Base64UrlEncode(randomBytes);
    }

    public string HashToken(string rawToken)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(rawToken.Trim()));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string Base64UrlEncode(byte[] input) =>
        Convert.ToBase64String(input)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    /// <summary>Decodes base64url and rejects non-canonical encodings (padding, stray bits), so a token has exactly one valid spelling.</summary>
    private static byte[] Base64UrlDecodeCanonical(string input)
    {
        byte[] bytes = Base64UrlDecode(input);
        if (!string.Equals(Base64UrlEncode(bytes), input, StringComparison.Ordinal))
        {
            throw new FormatException("Non-canonical base64url.");
        }

        return bytes;
    }

    private static byte[] Base64UrlDecode(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2:
                padded += "==";
                break;
            case 3:
                padded += "=";
                break;
        }

        return Convert.FromBase64String(padded);
    }
}
