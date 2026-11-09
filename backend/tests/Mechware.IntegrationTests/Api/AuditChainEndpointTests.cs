using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Mechware.Infrastructure.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Api;

/// <summary>GET /admin/audit/chain: staff with audit.view see whether the audit log is intact; nobody else does.</summary>
public sealed class AuditChainEndpointTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private async Task<HttpClient> SignInAsync(string email, string password, string? totpSecret = null)
    {
        HttpClient client = factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = email,
            password,
            totpCode = totpSecret is null ? null : new TotpService().ComputeTotpCode(totpSecret, DateTimeOffset.UtcNow)
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        return client;
    }

    [PostgresFact]
    public async Task Admins_SeeTheChainStatus_BuyersDoNot()
    {
        HttpClient admin = await SignInAsync("admin@mechware.com.bd", "MechAdmin#2026!", "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP");
        HttpResponseMessage ok = await admin.GetAsync("/api/v1/admin/audit/chain");
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        using (JsonDocument doc = JsonDocument.Parse(await ok.Content.ReadAsStringAsync()))
            doc.RootElement.GetProperty("data").GetProperty("intact").GetBoolean().Should().BeTrue();

        HttpClient buyer = await SignInAsync("buyer@mechware.com.bd", "BuyerPass#2026!");
        (await buyer.GetAsync("/api/v1/admin/audit/chain")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
