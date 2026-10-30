using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.UnitTests.Properties;

namespace ShopVerse.UnitTests.Identity;

/// <summary>
/// Attacker-model tests for access-token validation (ASVS 5.0 V9 self-contained tokens):
/// forged, tampered, replayed-after-expiry, confused-algorithm and malformed tokens must all be rejected
/// without throwing.
/// </summary>
public class JwtTamperingTests
{
    private const string Secret = "unit-test-signing-secret-at-least-32-chars!!";
    private const string Issuer = "https://api.mechware.com.bd";
    private const string Audience = "https://mechware.com.bd";
    private static readonly DateTime Now = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly long NowUnix = new DateTimeOffset(Now).ToUnixTimeSeconds();

    private readonly JwtTokenService _svc = new(Secret, Issuer, Audience);

    private static string B64(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
    private static string B64(string json) => B64(Encoding.UTF8.GetBytes(json));

    private static string Sign(string headerJson, string payloadJson, string key = Secret)
    {
        var input = $"{B64(headerJson)}.{B64(payloadJson)}";
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes(key), Encoding.ASCII.GetBytes(input));
        return $"{input}.{B64(sig)}";
    }

    private const string Hs256Header = """{"alg":"HS256","typ":"JWT"}""";

    private static string Payload(object? iss = null, object? aud = null, object? exp = null, bool omitIss = false, bool omitAud = false,
        bool omitExp = false, long? nbf = null, long? iat = null, string[]? permissions = null)
    {
        var d = new Dictionary<string, object?> { ["sub"] = Guid.NewGuid().ToString(), ["email"] = "buyer@example.test" };
        if (!omitIss) d["iss"] = iss ?? Issuer;
        if (!omitAud) d["aud"] = aud ?? Audience;
        if (!omitExp) d["exp"] = exp ?? NowUnix + 600;
        if (nbf is not null) d["nbf"] = nbf;
        d["iat"] = iat ?? NowUnix;
        d["roles"] = new[] { "Buyer" };
        d["permission"] = permissions ?? ["catalog.read"];
        return JsonSerializer.Serialize(d);
    }

    [Fact]
    public void GenuineToken_IsAccepted()
    {
        var user = User.Create("buyer@example.test", null, null, "Buyer");
        var token = _svc.GenerateAccessToken(user, ["Buyer"], ["catalog.read"], Now);
        _svc.ValidateAccessToken(token, Now.AddMinutes(14)).Should().NotBeNull();
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload()), Now).Should().NotBeNull();
    }

    [Fact]
    public void PrivilegeEscalation_ByEditingThePayload_IsRejected()
    {
        var user = User.Create("buyer@example.test", null, null, "Buyer");
        var parts = _svc.GenerateAccessToken(user, ["Buyer"], ["catalog.read"], Now).Split('.');
        var json = Encoding.UTF8.GetString(Convert.FromBase64String(parts[1].Replace('-', '+').Replace('_', '/').PadRight((parts[1].Length + 3) / 4 * 4, '=')));
        var escalated = json.Replace("\"catalog.read\"", "\"catalog.read\",\"orders.refund\",\"users.manage\"").Replace("\"Buyer\"", "\"SuperAdmin\"");
        escalated.Should().NotBe(json);

        _svc.ValidateAccessToken($"{parts[0]}.{B64(escalated)}.{parts[2]}", Now).Should().BeNull();
    }

    [Fact]
    public void ForgedSignature_WithAnotherKey_IsRejected() =>
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(), "attacker-controlled-secret-that-is-long-enough"), Now).Should().BeNull();

    [Theory]
    [InlineData("""{"alg":"none","typ":"JWT"}""")]
    [InlineData("""{"alg":"None"}""")]
    [InlineData("""{"alg":"HS512","typ":"JWT"}""")]
    [InlineData("""{"alg":"RS256","typ":"JWT"}""")]
    [InlineData("""{"typ":"JWT"}""")]
    [InlineData("""{"alg":"HS256","crit":["exp"]}""")]
    [InlineData("""not-json""")]
    public void AlgorithmConfusion_OrUnexpectedHeader_IsRejectedEvenWhenTheHmacMatches(string header) =>
        _svc.ValidateAccessToken(Sign(header, Payload()), Now).Should().BeNull();

    [Fact]
    public void UnsignedAlgNoneToken_IsRejected()
    {
        var token = $"{B64("""{"alg":"none"}""")}.{B64(Payload())}.";
        _svc.ValidateAccessToken(token, Now).Should().BeNull();
    }

    [Fact]
    public void ExpiredToken_IsRejected_IncludingTheExactExpirySecond()
    {
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(exp: NowUnix - 1)), Now).Should().BeNull();
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(exp: NowUnix)), Now).Should().BeNull();
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(omitExp: true)), Now).Should().BeNull();
    }

    [Fact]
    public void MissingOrWrongIssuerOrAudience_IsRejected()
    {
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(omitIss: true)), Now).Should().BeNull("iss is mandatory");
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(omitAud: true)), Now).Should().BeNull("aud is mandatory");
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(iss: "https://evil.example")), Now).Should().BeNull();
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(aud: "https://console.mechware.com.bd")), Now).Should().BeNull();
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(aud: new[] { "https://evil.example" })), Now).Should().BeNull();
    }

    [Fact]
    public void NotYetValidToken_IsRejected()
    {
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(nbf: NowUnix + 120)), Now).Should().BeNull();
        _svc.ValidateAccessToken(Sign(Hs256Header, Payload(iat: NowUnix + 3600)), Now).Should().BeNull("a token issued in the future means clock abuse or forgery");
    }

    [Theory]
    [InlineData("""{"exp":"tomorrow","iss":"https://api.mechware.com.bd","aud":"https://mechware.com.bd"}""")]
    [InlineData("""{"exp":1e400}""")]
    [InlineData("""{"exp":9999999999,"iss":42,"aud":"https://mechware.com.bd"}""")]
    [InlineData("""{"exp":9999999999,"iss":"https://api.mechware.com.bd","aud":{"x":1}}""")]
    [InlineData("""{"exp":9999999999,"iss":"https://api.mechware.com.bd","aud":"https://mechware.com.bd","sub":{"a":1},"roles":"Admin","permission":[1,2,{"x":"y"}]}""")]
    [InlineData("""[1,2,3]""")]
    [InlineData("""null""")]
    [InlineData("""{"exp":9999999999,""")]
    [InlineData("""￿""")]
    public void CorrectlySignedButMalformedPayload_NeverThrows(string payload)
    {
        var token = Sign(Hs256Header, payload);
        FluentActions.Invoking(() => _svc.ValidateAccessToken(token, Now)).Should().NotThrow();
    }

    [Fact]
    public void RandomGarbage_NeverThrowsAndIsNeverAccepted()
    {
        var genuine = Sign(Hs256Header, Payload());
        PropertyRunner.ForAll(601, 20_000, (rng, _) =>
        {
            string token = rng.Next(4) switch
            {
                0 => rng.NastyString(200),
                1 => string.Join('.', Enumerable.Range(0, rng.Next(0, 6)).Select(_ => rng.NastyString(40))),
                2 => Mutate(genuine, rng),
                _ => genuine[..rng.Next(0, genuine.Length)],
            };
            if (token == genuine) return;
            ShopVerse.Infrastructure.Identity.JwtTokenService svc = _svc;
            System.Security.Claims.ClaimsPrincipal? result = null;
            FluentActions.Invoking(() => result = svc.ValidateAccessToken(token, Now)).Should().NotThrow();
            result.Should().BeNull("mutated token '{0}' must not validate", token);
        });
    }

    [Fact]
    public void OversizedToken_IsRejectedWithoutParsing()
    {
        var huge = Sign(Hs256Header, Payload(permissions: Enumerable.Range(0, 5_000).Select(i => $"perm.{i}").ToArray()));
        huge.Length.Should().BeGreaterThan(JwtTokenService.MaxTokenLength);
        _svc.ValidateAccessToken(huge, Now).Should().BeNull();
    }

    private static string Mutate(string token, Random rng)
    {
        var chars = token.ToCharArray();
        var flips = rng.Next(1, 4);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789-_.";
        for (var i = 0; i < flips; i++)
        {
            var at = rng.Next(chars.Length);
            char replacement;
            do replacement = alphabet[rng.Next(alphabet.Length)]; while (replacement == chars[at]);
            chars[at] = replacement;
        }

        return new string(chars);
    }
}
