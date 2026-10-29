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
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        string[] parts = token.Trim().Split('.');
        if (parts.Length != 3)
        {
            return null;
        }

        string signingInput = $"{parts[0]}.{parts[1]}";
        byte[] expectedSignature = HMACSHA256.HashData(_signingKey, Encoding.ASCII.GetBytes(signingInput));

        byte[] providedSignature;
        byte[] payloadBytes;
        try
        {
            providedSignature = Base64UrlDecode(parts[2]);
            payloadBytes = Base64UrlDecode(parts[1]);
        }
        catch (FormatException)
        {
            return null;
        }

        if (!CryptographicOperations.FixedTimeEquals(expectedSignature, providedSignature))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(payloadBytes);
        JsonElement root = doc.RootElement;

        if (!root.TryGetProperty("exp", out JsonElement expEl) || !expEl.TryGetInt64(out long expUnix))
        {
            return null;
        }

        long nowUnix = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc)).ToUnixTimeSeconds();
        if (nowUnix >= expUnix)
        {
            return null;
        }

        if (root.TryGetProperty("iss", out JsonElement issEl) &&
            !string.Equals(issEl.GetString(), _issuer, StringComparison.Ordinal))
        {
            return null;
        }

        if (root.TryGetProperty("aud", out JsonElement audEl) &&
            !string.Equals(audEl.GetString(), _audience, StringComparison.Ordinal))
        {
            return null;
        }

        var claims = new List<Claim>();

        if (root.TryGetProperty("sub", out JsonElement subEl) && subEl.GetString() is string sub)
        {
            claims.Add(new Claim(ClaimTypes.NameIdentifier, sub));
            claims.Add(new Claim("sub", sub));
        }

        if (root.TryGetProperty("email", out JsonElement emailEl) && emailEl.GetString() is string email)
        {
            claims.Add(new Claim(ClaimTypes.Email, email));
        }

        if (root.TryGetProperty("name", out JsonElement nameEl) && nameEl.GetString() is string name)
        {
            claims.Add(new Claim(ClaimTypes.Name, name));
        }

        if (root.TryGetProperty(SellerIdClaimType, out JsonElement sellerEl) && sellerEl.GetString() is string sellerId)
        {
            claims.Add(new Claim(SellerIdClaimType, sellerId));
        }

        if (root.TryGetProperty("roles", out JsonElement rolesEl) && rolesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement roleItem in rolesEl.EnumerateArray())
            {
                if (roleItem.GetString() is string role && !string.IsNullOrWhiteSpace(role))
                {
                    claims.Add(new Claim(ClaimTypes.Role, role));
                }
            }
        }

        if (root.TryGetProperty(PermissionClaimType, out JsonElement permsEl) && permsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (JsonElement permItem in permsEl.EnumerateArray())
            {
                if (permItem.GetString() is string perm && !string.IsNullOrWhiteSpace(perm))
                {
                    claims.Add(new Claim(PermissionClaimType, perm));
                }
            }
        }

        var identity = new ClaimsIdentity(claims, "Bearer");
        return new ClaimsPrincipal(identity);
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
