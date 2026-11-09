using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Security;

/// <summary>
/// A company's office usually reaches us from one public IP. Signed-in users therefore get a budget each, anonymous traffic
/// is limited per IP, and a request over the limit is refused at once (429 + Retry-After) instead of hanging in a queue.
/// </summary>
public sealed class PerUserRateLimitTests : IDisposable
{
    private const string Password = "RateLimit#2026!";
    private const int UserBudget = 6;
    private const int AnonymousBudget = 4;
    private DbBackedFactory _factory = Factory();

    private static DbBackedFactory Factory(int ipCeiling = 1_000_000) => new(settings: new Dictionary<string, string>
    {
        ["RateLimiting:UserPermitPerMinute"] = UserBudget.ToString(),
        ["RateLimiting:GlobalPermitPerMinute"] = AnonymousBudget.ToString(),
        ["RateLimiting:IpCeilingPerMinute"] = ipCeiling.ToString(),
    });

    public void Dispose() => _factory.Dispose();

    private async Task<string> NewUserAsync()
    {
        string email = $"rl.{Guid.NewGuid():N}"[..20] + "@mechware.com.bd";
        await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);
        var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Rate Limit", "en");
        user.VerifyEmail(DateTime.UtcNow);
        user.AssignRole(Guid.NewGuid(), Permissions.Roles.Buyer);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return email;
    }

    private async Task<HttpClient> SignedInAsync(string email)
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        return client;
    }

    [PostgresFact]
    public async Task UsersBehindOneIp_EachHaveTheirOwnBudget_AndOverTheLimitIsRefusedAtOnce()
    {
        // Created before any request, so the two logins are the only anonymous calls.
        string aliceEmail = await NewUserAsync(), bobEmail = await NewUserAsync();
        HttpClient alice = await SignedInAsync(aliceEmail);
        HttpClient bob = await SignedInAsync(bobEmail);

        for (int i = 0; i < UserBudget; i++)
            (await alice.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK, $"call {i + 1} is within Alice's budget");

        var clock = Stopwatch.StartNew();
        HttpResponseMessage over = await alice.GetAsync("/api/v1/users/me");
        clock.Stop();
        over.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        over.Headers.RetryAfter.Should().NotBeNull();
        using (JsonDocument body = JsonDocument.Parse(await over.Content.ReadAsStringAsync()))
            body.RootElement.GetProperty("error").GetProperty("code").GetString().Should().Be("RATE_LIMITED", "the 429 uses the API's usual error envelope");
        clock.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(5), "over the limit is answered at once, not queued");

        // Same IP, different person: Bob's budget is untouched by Alice's.
        (await bob.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task AForgedToken_SharesTheAnonymousBudgetOfItsIp()
    {
        HttpClient plain = _factory.CreateClient();
        HttpClient forged = _factory.CreateClient();
        forged.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiJ4In0.bad");

        // Half the anonymous budget without a token, the rest with a made-up one: they come out of the same bucket.
        for (int i = 0; i < AnonymousBudget / 2; i++) (await plain.GetAsync("/api/v1/locations/bd")).StatusCode.Should().Be(HttpStatusCode.OK);
        for (int i = 0; i < AnonymousBudget - AnonymousBudget / 2; i++) (await forged.GetAsync("/api/v1/locations/bd")).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);
        (await forged.GetAsync("/api/v1/locations/bd")).StatusCode.Should().Be(HttpStatusCode.TooManyRequests,
            "a made-up token must not buy a budget of its own");
    }

    [PostgresFact]
    public async Task ManyAccountsBehindOneIp_CannotExceedTheIpCeiling()
    {
        _factory.Dispose();
        _factory = Factory(ipCeiling: 8);
        string a = await NewUserAsync(), b = await NewUserAsync();
        HttpClient alice = await SignedInAsync(a);   // 1 request
        HttpClient bob = await SignedInAsync(b);     // 2

        var statuses = new List<HttpStatusCode>();
        for (int i = 0; i < 4; i++) statuses.Add((await alice.GetAsync("/api/v1/users/me")).StatusCode);   // within Alice's 6
        for (int i = 0; i < 4; i++) statuses.Add((await bob.GetAsync("/api/v1/users/me")).StatusCode);     // within Bob's 6

        statuses.Take(6).Should().OnlyContain(s => s == HttpStatusCode.OK);
        statuses.Last().Should().Be(HttpStatusCode.TooManyRequests, "each stayed within their own budget, but the address went over its ceiling");
    }
}
