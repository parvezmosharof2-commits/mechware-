using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Security;

/// <summary>Threat model v2, P6: payment webhooks have their own rate-limit bucket, separate from the one that throttles browsers.</summary>
public class WebhookRateLimitTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private WebApplicationFactory<Program> Factory(int globalPerMinute, int webhookPerMinute) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseSetting("ConnectionStrings:DefaultConnection", fixture.ConnectionString);
            b.UseSetting("ConnectionStrings:Redis", TestInfra.RedisConnection);
            b.UseSetting("RateLimiting:GlobalPermitPerMinute", globalPerMinute.ToString());
            b.UseSetting("RateLimiting:WebhookPermitPerMinute", webhookPerMinute.ToString());
        });

    private static StringContent ForgedWebhook() => new("{\"id\":\"evt_x\",\"providerRef\":\"mock_x\"}", Encoding.UTF8, "application/json");

    [PostgresFact]
    public async Task ABurstOfWebhooks_IsNotThrottledByTheBrowserLimit_AndBrowsersAreNotStarvedByWebhooks()
    {
        await using var factory = Factory(globalPerMinute: 10, webhookPerMinute: 1000);
        using var client = factory.CreateClient();

        // 40 deliveries from one address, four times the browser budget: refused on the signature, never throttled.
        for (var i = 0; i < 40; i++)
            (await client.PostAsync("/api/v1/payments/webhooks/mock", ForgedWebhook())).StatusCode.Should().NotBe(HttpStatusCode.TooManyRequests);

        // The browser budget is untouched by them.
        (await client.GetAsync("/api/v1/locations/bd?level=division")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task TheWebhookBucket_HasItsOwnLimit()
    {
        await using var factory = Factory(globalPerMinute: 1000, webhookPerMinute: 5);
        using var client = factory.CreateClient();
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await client.PostAsync("/api/v1/payments/webhooks/mock", ForgedWebhook())).StatusCode);

        statuses.Take(5).Should().NotContain(HttpStatusCode.TooManyRequests);
        statuses.Skip(5).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
    }
}
