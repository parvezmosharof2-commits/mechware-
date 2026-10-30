using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using FluentAssertions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace ShopVerse.IntegrationTests.Security;

/// <summary>
/// Security regression suite (brief §11.1, §16): authorisation coverage of every route, forged-credential
/// rejection, brute-force throttling and error-leakage. New endpoints fail this suite until they are
/// either protected or consciously added to <see cref="PublicEndpoints"/>.
/// </summary>
public class ApiSecurityRegressionTests : IClassFixture<ApiSecurityRegressionTests.SweepFactory>
{
    /// <summary>The sweeps send hundreds of requests from one test client, so lift the per-IP global limit.</summary>
    public sealed class SweepFactory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder) =>
            builder.UseSetting("RateLimiting:GlobalPermitPerMinute", "1000000");
    }

    private readonly SweepFactory _factory;

    public ApiSecurityRegressionTests(SweepFactory factory) => _factory = factory;

    /// <summary>The reviewed anonymous surface. Adding to this list is a security review decision.</summary>
    private static readonly HashSet<string> PublicEndpoints =
    [
        "POST api/v1/auth/register",
        "POST api/v1/auth/otp/send",
        "POST api/v1/auth/verify-otp",
        "POST api/v1/auth/login",
        "POST api/v1/auth/google",
        "POST api/v1/auth/refresh",
        "POST api/v1/auth/logout",
        "POST api/v1/auth/password/forgot",
        "POST api/v1/auth/password/reset",
        "GET api/v1/locations/bd",
        "GET api/v1/storefront/products/{slug}",
        "GET api/v1/storefront/categories",
        "GET api/v1/storefront/categories/{slug}/products",
        "* /health/live",
        "* /health/ready",
    ];

    /// <summary>Anonymous endpoints that accept credentials or trigger messages must use the strict limiter.</summary>
    private static readonly HashSet<string> CredentialEndpoints =
    [
        "POST api/v1/auth/register",
        "POST api/v1/auth/otp/send",
        "POST api/v1/auth/verify-otp",
        "POST api/v1/auth/login",
        "POST api/v1/auth/google",
        "POST api/v1/auth/password/forgot",
        "POST api/v1/auth/password/reset",
    ];

    private sealed record Route(string Key, string Method, string Pattern, Endpoint Endpoint)
    {
        public bool IsAnonymous => Endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || Endpoint.Metadata.GetOrderedMetadata<IAuthorizeData>().Count == 0;
    }

    private List<Route> Routes()
    {
        var sources = _factory.Services.GetServices<EndpointDataSource>();
        return sources.SelectMany(s => s.Endpoints).OfType<RouteEndpoint>()
            .SelectMany(e =>
            {
                var methods = e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["*"];
                var pattern = e.RoutePattern.RawText ?? string.Empty;
                return methods.Select(m => new Route($"{m} {pattern}", m, pattern, e));
            })
            .Where(r => !r.Pattern.StartsWith("swagger", StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private static string Concrete(string pattern) =>
        "/" + Regex.Replace(pattern.TrimStart('/'), "{([^}:]+)(:[^}]+)?}", m =>
            m.Groups[2].Value.Contains("guid", StringComparison.OrdinalIgnoreCase) ? Guid.NewGuid().ToString() : "probe");

    [Fact]
    public void EveryEndpoint_IsProtectedOrOnTheReviewedPublicAllowList()
    {
        var routes = Routes();
        routes.Should().NotBeEmpty();

        var anonymous = routes.Where(r => r.IsAnonymous).Select(r => r.Key).ToHashSet();
        anonymous.Except(PublicEndpoints).Should().BeEmpty("every endpoint must be [Authorize]/[RequirePermission] unless reviewed as public");
        PublicEndpoints.Except(anonymous).Should().BeEmpty("stale allow-list entries hide mistakes; remove endpoints that are gone or now protected");
    }

    [Fact]
    public void CredentialEndpoints_UseTheStrictRateLimitPolicy()
    {
        var routes = Routes().ToDictionary(r => r.Key);
        foreach (var key in CredentialEndpoints)
        {
            routes.Should().ContainKey(key);
            routes[key].Endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName
                .Should().Be("AuthStrictPolicy", "{0} accepts credentials or sends messages", key);
        }
    }

    public static TheoryData<string?> ForgedAuthorizationHeaders => new()
    {
        null,
        "Bearer ",
        "Bearer garbage",
        "Bearer a.b.c",
        "Basic YWRtaW46YWRtaW4=",
        "Bearer " + ForgedToken("""{"alg":"none","typ":"JWT"}""", sign: false),
        "Bearer " + ForgedToken("""{"alg":"HS256","typ":"JWT"}""", sign: true),
    };

    [Theory]
    [MemberData(nameof(ForgedAuthorizationHeaders))]
    public async Task ProtectedEndpoints_RejectMissingOrForgedCredentials_With401(string? authorization)
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        var protectedRoutes = Routes().Where(r => !r.IsAnonymous && r.Method != "*").ToList();
        protectedRoutes.Should().NotBeEmpty();

        foreach (var route in protectedRoutes)
        {
            using var request = new HttpRequestMessage(new HttpMethod(route.Method), Concrete(route.Pattern));
            if (route.Method is "POST" or "PUT" or "PATCH")
                request.Content = new StringContent("{}", Encoding.UTF8, "application/json");
            if (authorization is not null)
                request.Headers.TryAddWithoutValidation("Authorization", authorization);

            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.Unauthorized, "{0} {1} must not be reachable with '{2}'", route.Method, route.Pattern, authorization ?? "<none>");
        }
    }

    /// <summary>A token with admin claims signed with a key that is not the server's.</summary>
    private static string ForgedToken(string header, bool sign)
    {
        static string B64(byte[] b) => Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var exp = DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeSeconds();
        var payload = $$"""{"sub":"{{Guid.NewGuid()}}","iss":"https://api.mechware.com.bd","aud":"https://mechware.com.bd","exp":{{exp}},"roles":["SuperAdmin"],"permission":["orders.refund","users.manage","sellers.manage.own"]}""";
        var input = $"{B64(Encoding.UTF8.GetBytes(header))}.{B64(Encoding.UTF8.GetBytes(payload))}";
        if (!sign) return input + ".";
        var sig = HMACSHA256.HashData(Encoding.UTF8.GetBytes("attacker-guessed-secret-0123456789abcdef"), Encoding.ASCII.GetBytes(input));
        return $"{input}.{B64(sig)}";
    }

    public static TheoryData<string, string> HostileBodies => new()
    {
        { "application/json", "{" },
        { "application/json", "{\"identifier\": {\"$ne\": null}, \"password\": [1,2,3]}" },
        { "application/json", "{\"identifier\":\"' OR 1=1 --\",\"password\":\"x\"}" },
        { "application/json", "[" + string.Concat(Enumerable.Repeat("[", 200)) + string.Concat(Enumerable.Repeat("]", 200)) + "]" },
        { "application/json", "{\"identifier\":\"" + new string('A', 200_000) + "\",\"password\":\"x\"}" },
        { "application/json", "\"\\ud800\"" },
        { "text/plain", "identifier=admin&password=admin" },
        { "application/xml", "<?xml version=\"1.0\"?><!DOCTYPE x [<!ENTITY e SYSTEM \"file:///etc/passwd\">]><x>&e;</x>" },
    };

    [Theory]
    [MemberData(nameof(HostileBodies))]
    public async Task HostileInput_NeverCausesA500_OrLeaksInternals(string contentType, string body)
    {
        var client = _factory.CreateClient();
        using var content = new StringContent(body, Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue(contentType);

        using var response = await client.PostAsync("/api/v1/auth/register", content);
        var text = await response.Content.ReadAsStringAsync();

        ((int)response.StatusCode).Should().BeLessThan(500, "hostile input must be rejected as a client error, body: {0}", text.Length > 300 ? text[..300] : text);
        text.Should().NotContainAny("StackTrace", "   at ShopVerse.", "Npgsql", "Exception:", "/home/", "System.Text.Json.JsonException");
    }

    [Theory]
    [InlineData("/api/v1/locations/bd?level=%27%3B%20DROP%20TABLE%20users%3B--")]
    [InlineData("/api/v1/locations/bd?parentId=not-a-guid")]
    [InlineData("/api/v1/locations/bd?level=" + "division%00")]
    [InlineData("/api/v1/../../etc/passwd")]
    [InlineData("/api/v1/auth/%2e%2e/%2e%2e/health/live")]
    public async Task HostileQueriesAndPaths_NeverCauseA500(string url)
    {
        var client = _factory.CreateClient();
        using var response = await client.GetAsync(url);
        ((int)response.StatusCode).Should().BeLessThan(500);
        (await response.Content.ReadAsStringAsync()).Should().NotContainAny("StackTrace", "   at ShopVerse.");
    }

    [Fact]
    public async Task Production_RefusesToStart_WithThePublishedDevelopmentJwtSecret()
    {
        await using var factory = ProductionFactory(jwtSecret: "super_secret_mechware_256bit_production_ready_jwt_key_here_for_local_dev");
        FluentActions.Invoking(() => factory.CreateClient()).Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("published development value"));
    }

    [Fact]
    public async Task Production_RefusesToStart_WithoutAJwtSecret()
    {
        await using var factory = ProductionFactory(jwtSecret: "");
        FluentActions.Invoking(() => factory.CreateClient()).Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("JWT Secret is required"));
    }

    [Fact]
    public async Task Production_HidesSwagger_AndThrottlesLoginBruteForce()
    {
        await using var factory = ProductionFactory(jwtSecret: "prod-like-secret-" + Guid.NewGuid().ToString("N"));
        var client = factory.CreateClient();

        (await client.GetAsync("/swagger/index.html")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await client.GetAsync("/swagger/v1/swagger.json")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++)
        {
            using var response = await client.PostAsync("/api/v1/auth/login",
                new StringContent("{\"identifier\":\"admin@mechware.com.bd\",\"password\":\"guess-" + i + "\"}", Encoding.UTF8, "application/json"));
            statuses.Add(response.StatusCode);
        }

        statuses.Take(5).Should().NotContain(HttpStatusCode.TooManyRequests);
        statuses.Skip(5).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests, "production allows 5 credential attempts per minute per IP");
    }

    private static readonly (string Email, string Password)[] PublishedDemoAccounts =
    [
        ("admin@mechware.com.bd", "MechAdmin#2026!"),
        ("seller.alpha@mechware.com.bd", "SellerAlpha#2026!"),
        ("seller.beta@mechware.com.bd", "SellerBeta#2026!"),
        ("buyer@mechware.com.bd", "BuyerPass#2026!")
    ];

    private static async Task<HttpStatusCode> LoginAsync(HttpClient client, string email, string password)
    {
        using var response = await client.PostAsync("/api/v1/auth/login",
            new StringContent($"{{\"identifier\":\"{email}\",\"password\":\"{password}\"}}", Encoding.UTF8, "application/json"));
        return response.StatusCode;
    }

    /// <summary>
    /// C1 in docs/security/THREAT_MODEL_COMPANIES_APPROVALS.md: the demo admin, with a password and TOTP secret
    /// published in this public repository, used to exist in every environment.
    /// </summary>
    [Fact]
    public async Task Production_HasNoDemoAccounts_EveryPublishedCredentialIsRefused()
    {
        await using var factory = ProductionFactory(jwtSecret: "prod-like-secret-" + Guid.NewGuid().ToString("N"));
        var client = factory.CreateClient();

        foreach (var (email, password) in PublishedDemoAccounts)
        {
            (await LoginAsync(client, email, password)).Should().Be(HttpStatusCode.Unauthorized, $"{email} must not exist in Production");
        }
    }

    [Fact]
    public async Task Staging_HasNoDemoAccountsEither()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Staging");
            b.UseSetting("Jwt:Secret", "staging-like-secret-" + Guid.NewGuid().ToString("N"));
            b.UseSetting("Storage:AccessKey", "test-access-key");
            b.UseSetting("Storage:SecretKey", "test-secret-key");
        });
        var client = factory.CreateClient();

        (await LoginAsync(client, "admin@mechware.com.bd", "MechAdmin#2026!")).Should().Be(HttpStatusCode.Unauthorized);
    }

    /// <summary>The control: without it a broken test could pass because the credentials were never valid.</summary>
    [Fact]
    public async Task Development_SeedsTheDemoAccounts_SoTheProductionCheckIsMeaningful()
    {
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.UseEnvironment("Development"));
        var client = factory.CreateClient();

        (await LoginAsync(client, "buyer@mechware.com.bd", "BuyerPass#2026!")).Should().Be(HttpStatusCode.OK);
        (await LoginAsync(client, "admin@mechware.com.bd", "MechAdmin#2026!")).Should().Be(HttpStatusCode.Accepted, "the admin account asks for its second factor");
    }

    private static WebApplicationFactory<Program> ProductionFactory(string jwtSecret) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Production");
            b.UseSetting("Jwt:Secret", jwtSecret);
            b.UseSetting("Storage:AccessKey", "test-access-key");
            b.UseSetting("Storage:SecretKey", "test-secret-key");
        });
}
