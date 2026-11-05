using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Api;

/// <summary>Threat model v2, P3: refund endpoints are closed to anonymous callers and to buyers.</summary>
public class RefundEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private static readonly (HttpMethod Method, string Path, object? Body)[] Endpoints =
    [
        (HttpMethod.Get, "/api/v1/admin/refunds", null),
        (HttpMethod.Post, $"/api/v1/admin/payments/{Guid.NewGuid()}/refunds", new { amountMinor = 100, reason = "Order cancelled" }),
        (HttpMethod.Post, $"/api/v1/admin/refunds/{Guid.NewGuid()}/approve", null),
        (HttpMethod.Post, $"/api/v1/admin/refunds/{Guid.NewGuid()}/reject", new { reason = "Not justified" })
    ];

    private static HttpRequestMessage Build((HttpMethod Method, string Path, object? Body) e) =>
        new(e.Method, e.Path) { Content = e.Body is null ? null : JsonContent.Create(e.Body) };

    [PostgresFact]
    public async Task Anonymous_Gets401_AndABuyer_Gets403_OnEveryRefundEndpoint()
    {
        using var anonymous = factory.CreateClient();
        foreach (var e in Endpoints)
            (await anonymous.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, e.Path);

        using var buyer = factory.CreateClient();
        var login = await buyer.PostAsJsonAsync("/api/v1/auth/login", new { identifier = "buyer@mechware.com.bd", password = "BuyerPass#2026!" });
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        buyer.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        foreach (var e in Endpoints)
            (await buyer.SendAsync(Build(e))).StatusCode.Should().Be(HttpStatusCode.Forbidden, e.Path);
    }
}
