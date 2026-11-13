using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

public sealed class CheckoutFollowUpsFactory() : DbBackedFactory(null, new Dictionary<string, string> { ["Cart:NewGuestCartsPerIpPerHour"] = "100000" });

/// <summary>
/// The cart store throws on every call (as when its Redis node is down) while everything else, including the stock locks that
/// order placement itself needs, works: only the cart clean-up may fail, never the order.
/// </summary>
public sealed class CheckoutCartStoreDownFactory() : DbBackedFactory(null, new Dictionary<string, string> { ["Cart:NewGuestCartsPerIpPerHour"] = "100000" })
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<Mechware.Application.Cart.ICartStore>();
            services.AddSingleton<Mechware.Application.Cart.ICartStore, ThrowingCartStore>();
        });
    }
}

internal sealed class ThrowingCartStore : Mechware.Application.Cart.ICartStore
{
    private static InvalidOperationException Down() => new("cart store is down");

    public Task<Mechware.Application.Cart.StoredCart?> GetAsync(Mechware.Application.Cart.CartOwner owner, CancellationToken ct = default) => throw Down();

    public Task<(Mechware.Application.Cart.CartSaveStatus Status, string? ETag)> SaveAsync(
        Mechware.Application.Cart.CartOwner owner, Mechware.Domain.Cart.Cart cart, string? expectedETag, CancellationToken ct = default) => throw Down();

    public Task DeleteAsync(Mechware.Application.Cart.CartOwner owner, CancellationToken ct = default) => throw Down();

    public Task<(Mechware.Application.Cart.CartSaveStatus Status, string? ETag)> SaveMergedAsync(
        Mechware.Application.Cart.CartOwner user, Mechware.Domain.Cart.Cart merged, string? expectedUserETag, Mechware.Application.Cart.CartOwner guest, string guestETag, CancellationToken ct = default) => throw Down();
}

internal static class CheckoutKit
{
    public const string Password = "Buyer#Test2026xyz";

    public static async Task<(HttpClient Client, Guid UserId)> BuyerInAZoneAsync(DbBackedFactory factory)
    {
        string email = $"buyer.{Guid.NewGuid():N}@example.test";
        Guid userId;
        await using (var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options))
        {
            var district = await ShippingTestKit.ZoneAsync(db, ShippingTestKit.Card());
            var user = User.Create(email, null, new PasswordHasherService().HashPassword(Password), "Test Buyer", "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
            db.Users.Add(user);
            db.Addresses.Add(ShippingTestKit.AddressIn(user.Id, district.NameEn, isDefault: true));
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var client = factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, userId);
    }

    public static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    public static string Lines(params (Guid Variant, int Qty)[] lines) =>
        "{ \"lines\": [" + string.Join(",", lines.Select(l => $$"""{ "variantId": "{{l.Variant}}", "quantity": {{l.Qty}} }""")) + "] }";

    public static async Task<HttpResponseMessage> PlaceAsync(HttpClient client, string body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = Json(body) };
        request.Headers.Add("Idempotency-Key", key ?? "k-" + Guid.NewGuid().ToString("N"));
        return await client.SendAsync(request);
    }

    public static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
}

/// <summary>E3b: what the checkout UI needed from the backend: a real total in the quote, the buyer's order list, and a cart that empties on placement.</summary>
public class OrderCheckoutFollowUpsApiTests(CheckoutFollowUpsFactory factory) : IClassFixture<CheckoutFollowUpsFactory>
{
    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private async Task<SeededVariant> SeedAsync(long price = 100_000)
    {
        await using var db = Db();
        return await CatalogTestData.SeedVariantAsync(db, 50, priceMinor: price);
    }

    [PostgresFact]
    public async Task TheQuotesGrandTotal_IsWhatPlacingTheSameCartTotals_AndEachSellerIsNamed()
    {
        var a = await SeedAsync(123_457);
        var b = await SeedAsync(98_765);
        var (client, _) = await CheckoutKit.BuyerInAZoneAsync(factory);
        var body = CheckoutKit.Lines((a.VariantId, 3), (b.VariantId, 2));

        using var quote = await client.PostAsync("/api/v1/orders/shipping-quote", CheckoutKit.Json(body));
        quote.StatusCode.Should().Be(HttpStatusCode.OK);
        var q = await CheckoutKit.DataAsync(quote);

        using var placedResponse = await CheckoutKit.PlaceAsync(client, body);
        placedResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var placed = await CheckoutKit.DataAsync(placedResponse);
        var order = await CheckoutKit.DataAsync(await client.GetAsync($"/api/v1/orders/{placed.GetProperty("orderId").GetGuid()}"));

        q.GetProperty("grandTotalMinor").GetInt64().Should().Be(placed.GetProperty("grandTotalMinor").GetInt64());
        q.GetProperty("grandTotalMinor").GetInt64().Should().Be(order.GetProperty("grandTotalMinor").GetInt64());
        q.GetProperty("taxMinor").GetInt64().Should().Be(order.GetProperty("taxMinor").GetInt64()).And.BeGreaterThan(0);
        q.GetProperty("totalMinor").GetInt64().Should().Be(order.GetProperty("shippingMinor").GetInt64()).And.BeGreaterThan(0);

        var perSeller = q.GetProperty("perSeller").EnumerateArray().ToList();
        perSeller.Should().HaveCount(2);
        await using var db = Db();
        foreach (var s in perSeller)
        {
            var name = await db.Sellers.Where(x => x.Id == s.GetProperty("sellerId").GetGuid()).Select(x => x.StoreName).SingleAsync();
            s.GetProperty("storeName").GetString().Should().Be(name).And.StartWith("Test Seller");
        }
    }

    [PostgresFact]
    public async Task TheOrderList_ShowsOnlyTheCallersOrders_NewestFirst_Paged_WithoutInternalFields()
    {
        var v = await SeedAsync();
        var (alice, _) = await CheckoutKit.BuyerInAZoneAsync(factory);
        var (bob, _) = await CheckoutKit.BuyerInAZoneAsync(factory);

        var aliceIds = new List<Guid>();
        for (int i = 1; i <= 3; i++)
        {
            using var r = await CheckoutKit.PlaceAsync(alice, CheckoutKit.Lines((v.VariantId, i)));
            r.StatusCode.Should().Be(HttpStatusCode.Created);
            aliceIds.Add((await CheckoutKit.DataAsync(r)).GetProperty("orderId").GetGuid());
        }
        using var bobOrder = await CheckoutKit.PlaceAsync(bob, CheckoutKit.Lines((v.VariantId, 1)));
        var bobId = (await CheckoutKit.DataAsync(bobOrder)).GetProperty("orderId").GetGuid();

        var list = await CheckoutKit.DataAsync(await alice.GetAsync("/api/v1/orders"));
        list.GetProperty("totalCount").GetInt32().Should().Be(3);
        var items = list.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetGuid()).Should().Equal(Enumerable.Reverse(aliceIds), "newest first").And.NotContain(bobId);
        items[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
            ["id", "orderNumber", "status", "grandTotalMinor", "createdAt", "itemCount", "companyId", "purchaseOrderNumber"],
            "the list carries no cost, margin, commission or seller-payout member");
        items[0].GetProperty("itemCount").GetInt32().Should().Be(1);
        items[0].GetProperty("status").GetString().Should().Be("pending_payment");

        var bobList = await CheckoutKit.DataAsync(await bob.GetAsync("/api/v1/orders"));
        bobList.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(bobId);

        var second = await CheckoutKit.DataAsync(await alice.GetAsync("/api/v1/orders?page=2&pageSize=2"));
        second.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(aliceIds[0]);

        var capped = await CheckoutKit.DataAsync(await alice.GetAsync("/api/v1/orders?pageSize=100000"));
        capped.GetProperty("pageSize").GetInt32().Should().Be(50);
        var floor = await CheckoutKit.DataAsync(await alice.GetAsync("/api/v1/orders?page=-4&pageSize=0"));
        floor.GetProperty("page").GetInt32().Should().Be(1);
        floor.GetProperty("pageSize").GetInt32().Should().Be(1);

        using var anonymous = await factory.CreateClient().GetAsync("/api/v1/orders");
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresAndRedisFact]
    public async Task PlacingAnOrder_RemovesExactlyTheOrderedLinesFromTheCart_AndAReplayDoesNotFail()
    {
        var ordered = await SeedAsync();
        var kept = await SeedAsync();
        var (client, _) = await CheckoutKit.BuyerInAZoneAsync(factory);
        foreach (var v in new[] { ordered, kept })
        {
            using var add = await client.PostAsync("/api/v1/cart/items", CheckoutKit.Json($$"""{ "variantId": "{{v.VariantId}}", "quantity": 2 }"""));
            add.StatusCode.Should().Be(HttpStatusCode.OK);
        }

        var body = CheckoutKit.Lines((ordered.VariantId, 2));
        const string key = "replay-key-0001";
        using var first = await CheckoutKit.PlaceAsync(client, body, key);
        first.StatusCode.Should().Be(HttpStatusCode.Created);

        async Task<List<Guid>> CartVariantsAsync() =>
            (await CheckoutKit.DataAsync(await client.GetAsync("/api/v1/cart"))).GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("variantId").GetGuid()).ToList();

        (await CartVariantsAsync()).Should().Equal(kept.VariantId);

        using var replay = await CheckoutKit.PlaceAsync(client, body, key);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await CartVariantsAsync()).Should().Equal(kept.VariantId);
    }
}

public class OrderCartCleanupStoreDownTests(CheckoutCartStoreDownFactory factory) : IClassFixture<CheckoutCartStoreDownFactory>
{
    [PostgresAndRedisFact]
    public async Task WhenTheCartStoreIsDown_TheOrderIsStillPlaced()
    {
        SeededVariant seeded;
        await using (var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options))
            seeded = await CatalogTestData.SeedVariantAsync(db, 50);
        var (client, _) = await CheckoutKit.BuyerInAZoneAsync(factory);

        using var placed = await CheckoutKit.PlaceAsync(client, CheckoutKit.Lines((seeded.VariantId, 1)));
        placed.StatusCode.Should().Be(HttpStatusCode.Created, await placed.Content.ReadAsStringAsync());

        var list = await CheckoutKit.DataAsync(await client.GetAsync("/api/v1/orders"));
        list.GetProperty("totalCount").GetInt32().Should().Be(1);
    }
}
