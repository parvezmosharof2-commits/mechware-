using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using ShopVerse.Application.Payments;
using ShopVerse.Domain.Payments;
using ShopVerse.Infrastructure.Payments;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

/// <summary>The order endpoint end to end over HTTP: auth, the Idempotency-Key header, and a client that tries to set its own price.</summary>
public class OrdersApiTests : IClassFixture<DefaultApiFactory>
{
    private readonly DefaultApiFactory _factory;

    public OrdersApiTests(DefaultApiFactory factory) => _factory = factory;

    private async Task<HttpClient> SignedInAsync(string identifier, string password)
    {
        var client = _factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private Task<HttpClient> BuyerAsync() => SignedInAsync("buyer@mechware.com.bd", "BuyerPass#2026!");

    private async Task<SeededVariant> SeedAsync(int onHand, long price)
    {
        var options = new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(_factory.ConnectionString).Options;
        await using var db = new ShopVerseDbContext(options);
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
    }

    private static HttpRequestMessage Post(string json, string? key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    private static string Body(Guid variant, int qty, string extra = "") =>
        $$"""{ "lines": [ { "variantId": "{{variant}}", "quantity": {{qty}} {{extra}} } ] }""";

    private static string NewKey() => "k-" + Guid.NewGuid().ToString("N");

    [PostgresFact]
    public async Task AClientThatSendsItsOwnPricesAndTotals_StillPaysTheServersPrice()
    {
        var v = await SeedAsync(onHand: 10, price: 250_000);
        var client = await BuyerAsync();

        // Every money-looking member a client might try, at the line level and at the top level.
        var tampered = $$"""
            { "grandTotalMinor": 1, "totalMinor": 1, "subtotalMinor": 1, "discountMinor": 999999999, "taxMinor": 0, "shippingMinor": 0,
              "lines": [ { "variantId": "{{v.VariantId}}", "quantity": 2, "unitPriceMinor": 1, "price": 1, "total": 1, "discountMinor": 500000, "vatRate": 0 } ] }
            """;
        using var response = await client.SendAsync(Post(tampered, NewKey()));

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var data = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("grandTotalMinor").GetInt64().Should().Be(2 * 250_000, "the server's price, not the 1 poisha the client claimed");

        var orderId = data.GetProperty("orderId").GetGuid();
        var order = (await (await client.GetAsync($"/api/v1/orders/{orderId}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        order.GetProperty("discountMinor").GetInt64().Should().Be(0);
        order.GetProperty("subtotalMinor").GetInt64().Should().Be(500_000);
        order.GetProperty("subOrders")[0].GetProperty("items")[0].GetProperty("unitPriceMinor").GetInt64().Should().Be(250_000);
    }

    [PostgresFact]
    public async Task TheIdempotencyKeyHeader_IsRequired_AndAReplayAnswers200WithTheSameOrder()
    {
        var v = await SeedAsync(10, 100_000);
        var client = await BuyerAsync();

        using var missing = await client.SendAsync(Post(Body(v.VariantId, 1), key: null));
        missing.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var key = NewKey();
        using var first = await client.SendAsync(Post(Body(v.VariantId, 1), key));
        using var again = await client.SendAsync(Post(Body(v.VariantId, 1), key));
        using var different = await client.SendAsync(Post(Body(v.VariantId, 2), key));

        first.StatusCode.Should().Be(HttpStatusCode.Created);
        first.Headers.Location.Should().NotBeNull();
        again.StatusCode.Should().Be(HttpStatusCode.OK, "a replay is not a new resource");
        different.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var a = (await first.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("orderId").GetGuid();
        var b = (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("orderId").GetGuid();
        b.Should().Be(a);
    }

    [PostgresFact]
    public async Task AnonymousCallers_AreRefused()
    {
        var v = await SeedAsync(5, 100_000);
        using var anonymous = _factory.CreateClient();
        using var post = await anonymous.SendAsync(Post(Body(v.VariantId, 1), NewKey()));
        using var get = await anonymous.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");
        post.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        get.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task SomeoneElsesOrder_LooksLikeItDoesNotExist()
    {
        var v = await SeedAsync(5, 100_000);
        var owner = await BuyerAsync();
        using var placed = await owner.SendAsync(Post(Body(v.VariantId, 1), NewKey()));
        var orderId = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("orderId").GetGuid();

        var other = await SignedInAsync("seller.alpha@mechware.com.bd", "SellerAlpha#2026!");
        using var peek = await other.GetAsync($"/api/v1/orders/{orderId}");
        using var unknown = await other.GetAsync($"/api/v1/orders/{Guid.NewGuid()}");

        peek.StatusCode.Should().Be(HttpStatusCode.NotFound);
        unknown.StatusCode.Should().Be(HttpStatusCode.NotFound, "the same answer for 'not yours' and 'does not exist'");
    }

    [PostgresFact]
    public async Task BadBodies_And_UnavailableStock_GetClearClientErrors()
    {
        var v = await SeedAsync(2, 100_000);
        var client = await BuyerAsync();

        using var zero = await client.SendAsync(Post(Body(v.VariantId, 0), NewKey()));
        using var empty = await client.SendAsync(Post("""{ "lines": [] }""", NewKey()));
        using var tooMany = await client.SendAsync(Post(Body(v.VariantId, 3), NewKey()));
        using var unknown = await client.SendAsync(Post(Body(Guid.NewGuid(), 1), NewKey()));

        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        empty.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        tooMany.StatusCode.Should().Be(HttpStatusCode.Conflict);
        unknown.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [PostgresFact]
    public async Task OrderingInACompanysName_NeedsMembership()
    {
        var v = await SeedAsync(5, 100_000);
        var client = await BuyerAsync();
        var someoneElsesCompany = Guid.NewGuid();

        using var response = await client.SendAsync(Post($$"""{ "companyId": "{{someoneElsesCompany}}", "lines": [ { "variantId": "{{v.VariantId}}", "quantity": 1 } ] }""", NewKey()));

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a company the caller does not belong to is simply not found");
    }

    [PostgresFact]
    public async Task PayingThroughTheMockGateway_ConfirmsTheOrderOnlyAfterAVerifiedSignedWebhook()
    {
        var v = await SeedAsync(onHand: 5, price: 100_000);
        var client = await BuyerAsync();
        using var placed = await client.SendAsync(Post(Body(v.VariantId, 1), NewKey()));
        var orderId = (await placed.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("orderId").GetGuid();

        // A caller cannot choose the amount: the body names only the provider.
        using var started = await client.PostAsJsonAsync($"/api/v1/orders/{orderId}/payments", new { provider = "mock", amountMinor = 1 });
        started.StatusCode.Should().Be(HttpStatusCode.Created);
        var session = (await started.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        session.GetProperty("amountMinor").GetInt64().Should().Be(100_000);
        var providerRef = session.GetProperty("redirectUrl").GetString()![(session.GetProperty("redirectUrl").GetString()!.LastIndexOf('/') + 1)..];

        // The customer finishes on the hosted page; the browser coming back changes nothing by itself.
        _factory.Services.GetServices<IPaymentProvider>().OfType<MockPaymentProvider>().Single().SimulatePayment(providerRef);
        var before = (await (await client.GetAsync($"/api/v1/orders/{orderId}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("status").GetString();
        before.Should().Be("pending_payment");

        var body = $$"""{ "id": "evt_{{Guid.NewGuid():N}}", "providerRef": "{{providerRef}}" }""";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        using var anonymous = _factory.CreateClient();
        using var forged = await anonymous.SendAsync(Webhook(body, ts, WebhookVerifier.Sign("wrong-secret", ts, body)));
        forged.StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        using var good = await anonymous.SendAsync(Webhook(body, ts, WebhookVerifier.Sign(MockPaymentProvider.DefaultSecret, ts, body)));
        good.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = (await (await client.GetAsync($"/api/v1/orders/{orderId}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("status").GetString();
        after.Should().Be("confirmed");

        using var again = await anonymous.SendAsync(Webhook(body, ts, WebhookVerifier.Sign(MockPaymentProvider.DefaultSecret, ts, body)));
        again.StatusCode.Should().Be(HttpStatusCode.OK, "a retried delivery is acknowledged, not an error");
        (await again.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("outcome").GetString().Should().Be("Duplicate");
    }

    private static HttpRequestMessage Webhook(string body, long timestamp, string signature)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/payments/webhooks/mock") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        request.Headers.Add("X-Timestamp", timestamp.ToString());
        request.Headers.Add("X-Signature", signature);
        return request;
    }
}
