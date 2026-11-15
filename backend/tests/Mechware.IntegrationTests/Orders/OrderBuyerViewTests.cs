using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Orders;
using Mechware.Application.Payments;
using Mechware.Domain.Payments;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Payments;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>E3c: what the buyer's order pages need: lookup by order number, store names, the ship-to snapshot and a payment summary, with nothing internal.</summary>
public class OrderBuyerViewTests(CheckoutFollowUpsFactory factory) : IClassFixture<CheckoutFollowUpsFactory>
{
    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private async Task<SeededVariant> SeedAsync(long price = 100_000)
    {
        await using var db = Db();
        return await CatalogTestData.SeedVariantAsync(db, 50, priceMinor: price);
    }

    private async Task<(Guid Id, string Number)> PlaceAsync(HttpClient client, params (Guid Variant, int Qty)[] lines)
    {
        using var r = await CheckoutKit.PlaceAsync(client, CheckoutKit.Lines(lines));
        r.StatusCode.Should().Be(HttpStatusCode.Created);
        var data = await CheckoutKit.DataAsync(r);
        return (data.GetProperty("orderId").GetGuid(), data.GetProperty("orderNumber").GetString()!);
    }

    private static async Task<string> RawAsync(HttpResponseMessage response) => await response.Content.ReadAsStringAsync();

    [PostgresFact]
    public async Task ByNumber_ReturnsTheSameOrderAsById_ForTheOwner()
    {
        var v = await SeedAsync();
        var (client, _) = await CheckoutKit.BuyerInAZoneAsync(factory);
        var (id, number) = await PlaceAsync(client, (v.VariantId, 2));

        using var byId = await client.GetAsync($"/api/v1/orders/{id}");
        using var byNumber = await client.GetAsync($"/api/v1/orders/by-number/{number}");

        byNumber.StatusCode.Should().Be(HttpStatusCode.OK);
        (await RawAsync(byNumber)).Should().Be(await RawAsync(byId));
    }

    [PostgresFact]
    public async Task ByNumber_ForAStrangersOrder_AnUnknownNumberAndABadFormat_AreTheSame404()
    {
        var v = await SeedAsync();
        var (alice, _) = await CheckoutKit.BuyerInAZoneAsync(factory);
        var (bob, _) = await CheckoutKit.BuyerInAZoneAsync(factory);
        var (_, aliceNumber) = await PlaceAsync(alice, (v.VariantId, 1));

        var answers = new List<(HttpStatusCode Status, string Body)>();
        foreach (var number in new[] { aliceNumber, "MW-260101-ZZZZZZ", "MW-26O101-ABCDEF", "not-a-number", "mw-260101-abcdef", "MW-260101-ABCDEF-X" })
        {
            using var r = await bob.GetAsync($"/api/v1/orders/by-number/{number}");
            // "instance" echoes the caller's own path and "traceId" is per request; everything else must be identical.
            var problem = JsonDocument.Parse(await RawAsync(r)).RootElement;
            answers.Add((r.StatusCode, $"{problem.GetProperty("error").GetProperty("code")}|{problem.GetProperty("title")}|{problem.GetProperty("detail")}|{problem.GetProperty("status")}|{problem.GetProperty("type")}"));
        }

        answers.Select(a => a.Status).Should().OnlyContain(s => s == HttpStatusCode.NotFound);
        answers.Select(a => a.Body).Distinct().Should().HaveCount(1, "a stranger's order must look exactly like a missing one");

        using var anonymous = await factory.CreateClient().GetAsync($"/api/v1/orders/by-number/{aliceNumber}");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task TheOrder_NamesEachStore_AndShowsTheShipToItWasPlacedWith_EvenAfterTheAddressBookChanges()
    {
        var a = await SeedAsync(123_457);
        var b = await SeedAsync(98_765);
        var (client, userId) = await CheckoutKit.BuyerInAZoneAsync(factory);
        var (id, _) = await PlaceAsync(client, (a.VariantId, 1), (b.VariantId, 1));

        await using (var db = Db())
        {
            await db.Addresses.Where(x => x.UserId == userId)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.Line1, "Changed later").SetProperty(x => x.RecipientName, "Someone Else"));
        }

        using var response = await client.GetAsync($"/api/v1/orders/{id}");
        string raw = await RawAsync(response);
        var order = JsonDocument.Parse(raw).RootElement.GetProperty("data");

        var subs = order.GetProperty("subOrders").EnumerateArray().ToList();
        subs.Should().HaveCount(2);
        await using (var db = Db())
            foreach (var s in subs)
            {
                var expected = await db.Sellers.Where(x => x.Id == s.GetProperty("sellerId").GetGuid()).Select(x => x.StoreName).SingleAsync();
                s.GetProperty("storeName").GetString().Should().Be(expected).And.StartWith("Test Seller");
            }
        subs.Select(s => s.GetProperty("storeName").GetString()).Distinct().Should().HaveCount(2);

        var shipTo = order.GetProperty("shipTo");
        shipTo.GetProperty("recipientName").GetString().Should().Be("Engr. Receiver");
        shipTo.GetProperty("line1").GetString().Should().Be("Road 1, Plot 2");
        shipTo.GetProperty("upazila").GetString().Should().Be("Sadar");
        shipTo.GetProperty("division").GetString().Should().Be("Dhaka");
        shipTo.GetProperty("postalCode").GetString().Should().Be("1000");
        shipTo.GetProperty("phone").GetString().Should().Be("+8801711000000");
        shipTo.GetProperty("district").GetString().Should().NotBeNullOrEmpty();
        shipTo.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(
            ["recipientName", "line1", "line2", "upazila", "district", "division", "postalCode", "phone", "siteName"]);
        raw.Should().NotContain("Changed later").And.NotContain("sourceId").And.NotContain("districtId");
    }

    [PostgresFact]
    public async Task Payment_IsNullBeforePaying_ThenPending_ThenCaptured_WithNoGatewayInternals()
    {
        var v = await SeedAsync();
        var (client, userId) = await CheckoutKit.BuyerInAZoneAsync(factory);
        var (id, number) = await PlaceAsync(client, (v.VariantId, 2));

        async Task<(string Raw, JsonElement Order)> ReadAsync()
        {
            using var r = await client.GetAsync($"/api/v1/orders/by-number/{number}");
            string raw = await RawAsync(r);
            return (raw, JsonDocument.Parse(raw).RootElement.GetProperty("data"));
        }

        var before = await ReadAsync();
        before.Order.GetProperty("payment").ValueKind.Should().Be(JsonValueKind.Null);

        var mock = new MockPaymentProvider();
        string providerRef;
        await using (var db = Db())
        {
            var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
            var payments = new PaymentService(db, [mock], inv, TimeProvider.System, ShippingTestKit.Deadlines(db));
            var session = await payments.StartAsync(userId, id, "mock");
            providerRef = session.RedirectUrl[(session.RedirectUrl.LastIndexOf('/') + 1)..];
        }

        var pending = await ReadAsync();
        var p = pending.Order.GetProperty("payment");
        p.GetProperty("status").GetString().Should().Be("pending");
        p.GetProperty("method").GetString().Should().Be("mock");
        p.GetProperty("amountMinor").GetInt64().Should().Be(pending.Order.GetProperty("grandTotalMinor").GetInt64());
        p.GetProperty("attempts").GetInt32().Should().Be(1);
        p.GetProperty("paidAt").ValueKind.Should().Be(JsonValueKind.Null);

        mock.SimulatePayment(providerRef);
        var body = $$"""{ "id": "evt_{{Guid.NewGuid():N}}", "providerRef": "{{providerRef}}" }""";
        long ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await using (var db = Db())
        {
            var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
            var payments = new PaymentService(db, [mock], inv, TimeProvider.System, ShippingTestKit.Deadlines(db));
            (await payments.HandleWebhookAsync("mock", WebhookVerifier.Sign(MockPaymentProvider.DefaultSecret, ts, body), ts.ToString(), body))
                .Should().Be(WebhookOutcome.Confirmed);
        }

        var paid = await ReadAsync();
        var q = paid.Order.GetProperty("payment");
        q.GetProperty("status").GetString().Should().Be("captured");
        q.GetProperty("paidAt").GetDateTime().Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(5));
        q.EnumerateObject().Select(x => x.Name).Should().BeEquivalentTo(["status", "method", "amountMinor", "paidAt", "attempts"]);

        foreach (var raw in new[] { pending.Raw, paid.Raw })
        {
            raw.Should().NotContain(providerRef).And.NotContain("redirectUrl").And.NotContain("providerRef").And.NotContain("transaction");
            raw.ToLowerInvariant().Should().NotContain("\"bin\"").And.NotContain("\"tin\"").And.NotContain("payload").And.NotContain("commission");
        }
    }

    [PostgresFact]
    public async Task TheOrderList_StaysOneCountAndOneRowQuery_NoMatterHowManyOrders()
    {
        var v = await SeedAsync();
        var (client, userId) = await CheckoutKit.BuyerInAZoneAsync(factory);
        await PlaceAsync(client, (v.VariantId, 1));

        async Task<int> ListQueriesAsync()
        {
            var recorder = new SqlRecorder();
            await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).AddInterceptors(recorder).Options);
            var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
            var orders = new OrderService(db, inv, TimeProvider.System, ShippingTestKit.DevelopmentQuoter(db), new Mechware.Infrastructure.Pricing.PriceListResolver(db));
            await orders.ListAsync(userId, 1, 20);
            return recorder.Commands.Count;
        }

        int one = await ListQueriesAsync();
        await PlaceAsync(client, (v.VariantId, 1));
        await PlaceAsync(client, (v.VariantId, 1));
        int three = await ListQueriesAsync();

        one.Should().BeLessThanOrEqualTo(2);
        three.Should().Be(one);
    }
}
