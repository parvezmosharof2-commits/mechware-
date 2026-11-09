using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Infrastructure.Identity;

namespace Mechware.IntegrationTests.Identity;

/// <summary>
/// A Google ID token is believed only when Google signed it, for this application, and it is still valid. Every claim in an
/// unsigned or self-signed token is the attacker's choice, the e-mail above all.
/// </summary>
public class GoogleIdTokenVerifierTests
{
    private static readonly DateTime Now = DateTime.UtcNow;
    private readonly StaticGoogleKeys _keys = new(new Dictionary<string, RSAParameters> { [IdentityTestKit.GoogleKid] = IdentityTestKit.GoogleKey.ExportParameters(false) });

    private GoogleIdTokenVerifier Verifier() => new(IdentityTestKit.GoogleClientId, _keys);

    private static Dictionary<string, object?> Claims() => IdentityTestKit.GoogleClaims("google-uid-1", "Owner@Example.com");

    private Task<Mechware.Application.Identity.GoogleIdentity> Verify(string token) => Verifier().VerifyAsync(token, Now);

    private static string B64(string s) => Convert.ToBase64String(Encoding.UTF8.GetBytes(s)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    [Fact]
    public async Task AGoogleSignedToken_GivesTheVerifiedIdentity()
    {
        var identity = await Verify(IdentityTestKit.GoogleToken(Claims()));
        identity.Subject.Should().Be("google-uid-1");
        identity.Email.Should().Be("owner@example.com");
    }

    [Fact]
    public async Task ATokenSignedWithAnotherKey_IsRefused()
    {
        using var attacker = RSA.Create(2048);
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(Claims(), signer: attacker));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*signature*");
    }

    [Fact]
    public async Task ChangingAClaimAfterSigning_BreaksTheSignature()
    {
        string[] parts = IdentityTestKit.GoogleToken(Claims()).Split('.');
        var forged = Claims(); forged["email"] = "victim@example.com";
        string tampered = $"{parts[0]}.{B64(JsonSerializer.Serialize(forged))}.{parts[2]}";

        Func<Task> act = () => Verify(tampered);
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*signature*");
    }

    [Theory]
    [InlineData("none")]
    [InlineData("HS256")]
    [InlineData("RS512")]
    public async Task OnlyRs256_IsAccepted(string alg)
    {
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(Claims(), alg: alg));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*RS256*");
    }

    [Fact]
    public async Task AnUnsignedToken_IsRefused()
    {
        string unsigned = $"{B64("{\"alg\":\"none\",\"kid\":\"" + IdentityTestKit.GoogleKid + "\"}")}.{B64(JsonSerializer.Serialize(Claims()))}.";
        Func<Task> act = () => Verify(unsigned);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Theory]
    [InlineData("google_oidc_anything-at-all")]
    [InlineData("ya29.anything-at-all")]
    [InlineData("a.b.sig_verified")]
    [InlineData("not-a-token")]
    public async Task TheOldMockFormats_AreRefused(string token)
    {
        Func<Task> act = () => Verify(token);
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task AnUnknownKeyId_TriesOneRefresh_ThenRefuses()
    {
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(Claims(), kid: "unknown-kid"));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*signature*");
        _keys.Refreshes.Should().Be(1);
    }

    [Fact]
    public async Task ATokenForAnotherApplication_IsRefused()
    {
        var claims = Claims(); claims["aud"] = "someone-else.apps.googleusercontent.com";
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(claims));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*different application*");

        claims["aud"] = new[] { IdentityTestKit.GoogleClientId };
        act = () => Verify(IdentityTestKit.GoogleToken(claims));
        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task AnotherIssuer_IsRefused()
    {
        var claims = Claims(); claims["iss"] = "https://evil-idp.example.com";
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(claims));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*issuer*");
    }

    [Fact]
    public async Task AnExpiredToken_IsRefused_AndOneWithoutExpiry_Too()
    {
        var claims = Claims(); claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-5).ToUnixTimeSeconds();
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(claims));
        await act.Should().ThrowAsync<UnauthorizedAccessException>();

        claims.Remove("exp");
        act = () => Verify(IdentityTestKit.GoogleToken(claims));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*expiry*");

        claims["exp"] = "2099-01-01";   // not a number: refused as a bad token, never a server error
        act = () => Verify(IdentityTestKit.GoogleToken(claims));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*expiry*");
    }

    [Fact]
    public async Task ATokenIssuedInTheFuture_IsRefused()
    {
        var claims = Claims(); claims["iat"] = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds();
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(claims));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*future*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public async Task AnEmailGoogleHasNotVerified_IsRefused(bool? verified)
    {
        var claims = Claims();
        if (verified is null) claims.Remove("email_verified"); else claims["email_verified"] = verified;
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(claims));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*verified*");
    }

    [Fact]
    public async Task AnOversizedToken_IsRefused_EvenWhenGoogleSignedIt()
    {
        var claims = Claims(); claims["name"] = new string('n', GoogleIdTokenVerifier.MaxTokenLength);
        Func<Task> act = () => Verify(IdentityTestKit.GoogleToken(claims));
        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*required*");
    }

    [Fact]
    public void TheJwksParser_ReadsRsaKeys_AndSkipsOthers()
    {
        var p = IdentityTestKit.GoogleKey.ExportParameters(false);
        string n = Convert.ToBase64String(p.Modulus!).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string e = Convert.ToBase64String(p.Exponent!).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        string json = $$"""{"keys":[{"kty":"RSA","kid":"k1","n":"{{n}}","e":"{{e}}","alg":"RS256"},{"kty":"EC","kid":"k2","x":"a","y":"b"},{"kty":"RSA","kid":"k3","n":"%%%","e":"AQAB"},{"kty":7,"kid":"k4"},"junk"]}""";

        var keys = GoogleJwksSigningKeys.ParseJwks(JsonDocument.Parse(json));
        keys.Keys.Should().Equal("k1");
        keys["k1"].Modulus.Should().Equal(p.Modulus);
    }

}

/// <summary>How Google's key set is fetched and cached: it must neither hammer Google nor go without keys longer than needed.</summary>
public class GoogleJwksSigningKeysTests
{
    private sealed class Clock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = new(2026, 11, 5, 12, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class Google : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public bool Down { get; set; }
        public string Kid { get; set; } = "k1";

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            request.RequestUri!.ToString().Should().Be(GoogleJwksSigningKeys.JwksUrl);
            if (Down) return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));

            var p = IdentityTestKit.GoogleKey.ExportParameters(false);
            static string B(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent($$"""{"keys":[{"kty":"RSA","kid":"{{Kid}}","n":"{{B(p.Modulus!)}}","e":"{{B(p.Exponent!)}}"}]}"""),
            };
            response.Headers.CacheControl = new System.Net.Http.Headers.CacheControlHeaderValue { Public = true, MaxAge = TimeSpan.FromMinutes(30) };
            return Task.FromResult(response);
        }
    }

    private readonly Clock _clock = new();
    private readonly Google _google = new();
    private GoogleJwksSigningKeys Keys() => new(() => new HttpClient(_google, disposeHandler: false), _clock);

    [Fact]
    public async Task KeysAreCached_ForAsLongAsGoogleSays()
    {
        var keys = Keys();
        (await keys.GetAsync(false, default)).Should().ContainKey("k1");
        await keys.GetAsync(false, default);
        _google.Calls.Should().Be(1);

        _clock.Now += TimeSpan.FromMinutes(31);
        _google.Kid = "k2";
        (await keys.GetAsync(false, default)).Should().ContainKey("k2");
        _google.Calls.Should().Be(2);
    }

    [Fact]
    public async Task AskingForARefresh_FetchesAtMostOnceAMinute()
    {
        var keys = Keys();
        await keys.GetAsync(false, default);
        for (int i = 0; i < 20; i++) await keys.GetAsync(true, default);
        _google.Calls.Should().Be(1, "forged key ids must not become a flood of requests to Google");

        _clock.Now += TimeSpan.FromMinutes(1);
        await keys.GetAsync(true, default);
        _google.Calls.Should().Be(2);
    }

    [Fact]
    public async Task WithNothingCached_AnOutageFailsClosed_AndFast()
    {
        var keys = Keys();
        _google.Down = true;
        for (int i = 0; i < 5; i++)
        {
            Func<Task> act = async () => await keys.GetAsync(false, default);
            await act.Should().ThrowAsync<InvalidOperationException>();
        }
        _google.Calls.Should().Be(1, "callers during the back-off get the failure at once instead of each waiting on Google");

        _clock.Now += TimeSpan.FromSeconds(11);
        _google.Down = false;
        (await keys.GetAsync(false, default)).Should().ContainKey("k1");
    }

    [Fact]
    public async Task DuringAnOutage_TheLastGoodKeysKeepWorking()
    {
        var keys = Keys();
        await keys.GetAsync(false, default);
        _clock.Now += TimeSpan.FromMinutes(31);
        _google.Down = true;

        (await keys.GetAsync(false, default)).Should().ContainKey("k1");
        (await keys.GetAsync(false, default)).Should().ContainKey("k1");
        _google.Calls.Should().Be(2, "after a failed refresh the next try waits a minute");
    }
}
