using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mechware.Application.Cart;
using Mechware.Domain.Common;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Cart;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;
using DomainCart = Mechware.Domain.Cart.Cart;

namespace Mechware.IntegrationTests.Cart;

/// <summary>Allows plenty of new guest carts per IP: most tests mint one each from the same (test) address.</summary>
public sealed class CartApiFactory() : DbBackedFactory(null, new Dictionary<string, string> { ["Cart:NewGuestCartsPerIpPerHour"] = "100000" });

/// <summary>A tiny limit, to prove the per-IP cap on new guest carts.</summary>
public sealed class CartLimitFactory() : DbBackedFactory(null, new Dictionary<string, string> { ["Cart:NewGuestCartsPerIpPerHour"] = "3" });

/// <summary>Redis is unreachable: the cart must say 503, never "empty".</summary>
public sealed class CartRedisDownFactory() : DbBackedFactory(null, new Dictionary<string, string>
{
    ["Cart:NewGuestCartsPerIpPerHour"] = "100000",
    ["ConnectionStrings:Redis"] = "127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=500,asyncTimeout=500"
});

/// <summary>Wraps the real store; lets a test count saves and force conflicts.</summary>
public sealed class ScriptedCartStore(ICartStore inner) : ICartStore
{
    public int Saves;
    public int Gets;
    /// <summary>Number of next saves to answer with Conflict (int.MaxValue = always).</summary>
    public int ConflictsToInject;

    public Task<StoredCart?> GetAsync(CartOwner owner, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Gets);
        return inner.GetAsync(owner, ct);
    }

    public Task<(CartSaveStatus Status, string? ETag)> SaveAsync(CartOwner owner, DomainCart cart, string? expectedETag, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Saves);
        if (Interlocked.Decrement(ref ConflictsToInject) >= 0) return Task.FromResult<(CartSaveStatus, string?)>((CartSaveStatus.Conflict, null));
        Interlocked.Exchange(ref ConflictsToInject, 0);
        return inner.SaveAsync(owner, cart, expectedETag, ct);
    }

    public Task DeleteAsync(CartOwner owner, CancellationToken ct = default) => inner.DeleteAsync(owner, ct);

    public Task<(CartSaveStatus Status, string? ETag)> SaveMergedAsync(
        CartOwner user, DomainCart merged, string? expectedUserETag, CartOwner guest, string guestETag, CancellationToken ct = default)
    {
        Interlocked.Increment(ref Saves);
        return inner.SaveMergedAsync(user, merged, expectedUserETag, guest, guestETag, ct);
    }
}

/// <summary>
/// E1.3: the cart over HTTP, against real PostgreSQL (prices, stock) and Redis (the cart). Cookies are handled by hand so a test
/// can forge, replay or omit them.
/// </summary>
public class CartApiTests : IClassFixture<CartApiFactory>
{
    private const string Cookie = "__Host-mw_cart";
    private const string Password = "Buyer#Test2026xyz";

    private readonly CartApiFactory _factory;

    public CartApiTests(CartApiFactory factory) => _factory = factory;

    // ---------------------------------------------------------------- helpers

    private static WebApplicationFactoryClientOptions NoCookies => new() { HandleCookies = false };

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private async Task<SeededVariant> SeedAsync(int onHand = 100, long price = 250_000)
    {
        await using var db = Db();
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
    }

    private async Task SqlAsync(FormattableString sql)
    {
        await using var db = Db();
        await db.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    private async Task<(HttpClient Client, Guid UserId)> BuyerAsync(WebApplicationFactory<Program>? factory = null)
    {
        factory ??= _factory;
        string email = $"buyer.{Guid.NewGuid():N}@example.test";
        Guid userId;
        await using (var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options))
        {
            var user = User.Create(email, null, new PasswordHasherService().HashPassword(Password), "Test Buyer", "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            userId = user.Id;
        }

        var client = factory.CreateClient(NoCookies);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return (client, userId);
    }

    private static async Task<HttpResponseMessage> CallAsync(
        HttpClient client, HttpMethod method, string url, object? body = null, string? cookie = null, params (string Name, string Value)[] headers)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        foreach (var (name, value) in headers) request.Headers.TryAddWithoutValidation(name, value);
        return await client.SendAsync(request);
    }

    /// <summary>The cart cookie as a request header value ("name=value"), or null when the response did not set it.</summary>
    private static string? CartCookie(HttpResponseMessage response) =>
        SetCookies(response).FirstOrDefault(c => c.StartsWith(Cookie + "=", StringComparison.Ordinal))?.Split(';')[0];

    private static List<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private static JsonElement Line(JsonElement cart, Guid variantId) =>
        cart.GetProperty("lines").EnumerateArray().Single(l => l.GetProperty("variantId").GetGuid() == variantId);

    private static string[] NoticeKinds(JsonElement cart) =>
        [.. cart.GetProperty("notices").EnumerateArray().Select(n => n.GetProperty("kind").GetString()!)];

    private static string Qty(int quantity) => $$"""{ "quantity": {{quantity}} }""";

    private static string Add(Guid variantId, int quantity) => $$"""{ "variantId": "{{variantId}}", "quantity": {{quantity}} }""";

    private async Task<(HttpClient Client, string Cookie)> GuestWithAsync(Guid variantId, int quantity = 1)
    {
        var client = _factory.CreateClient(NoCookies);
        using var added = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(variantId, quantity));
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        return (client, CartCookie(added)!);
    }

    // ---------------------------------------------------------------- the exit test: prices are the server's

    [PostgresAndRedisFact]
    public async Task AClientThatSendsItsOwnPricesAndTotals_StillGetsTheServersPrice()
    {
        var v = await SeedAsync(price: 250_000);
        var client = _factory.CreateClient(NoCookies);

        var tampered = $$"""
            { "variantId": "{{v.VariantId}}", "quantity": 2, "price": 1, "unitPrice": 1, "unitPriceMinor": 1, "seenUnitNetMinor": 1,
              "lineTotalMinor": 1, "total": 1, "subtotalMinor": 1, "totalMinor": 1, "discountMinor": 99999999, "vatRate": 0 }
            """;
        using var added = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", tampered);
        added.StatusCode.Should().Be(HttpStatusCode.OK);
        var cart = await DataAsync(added);
        Line(cart, v.VariantId).GetProperty("unitPriceMinor").GetInt64().Should().Be(250_000);
        Line(cart, v.VariantId).GetProperty("lineTotalMinor").GetInt64().Should().Be(500_000);
        cart.GetProperty("subtotalMinor").GetInt64().Should().Be(500_000);

        string cookie = CartCookie(added)!;
        using var set = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{v.VariantId}",
            """{ "quantity": 3, "price": 1, "unitPriceMinor": 1, "total": 1, "subtotalMinor": 1 }""", cookie);
        var afterSet = await DataAsync(set);
        afterSet.GetProperty("subtotalMinor").GetInt64().Should().Be(750_000);

        // The price lives in the catalog: when it changes, the next read shows the new price and says so.
        await SqlAsync($"UPDATE product_variants SET price_minor = 300000 WHERE id = {v.VariantId}");
        using var read = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        var fresh = await DataAsync(read);
        Line(fresh, v.VariantId).GetProperty("unitPriceMinor").GetInt64().Should().Be(300_000);
        fresh.GetProperty("subtotalMinor").GetInt64().Should().Be(900_000);
        NoticeKinds(fresh).Should().Contain("priceChanged");
        var notice = fresh.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "priceChanged");
        notice.GetProperty("was").GetInt64().Should().Be(250_000);
        notice.GetProperty("now").GetInt64().Should().Be(300_000);
    }

    // ---------------------------------------------------------------- guest identity

    [PostgresAndRedisFact]
    public async Task ReadingWithoutAToken_CreatesNoCart()
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICartStore>();
            s.AddSingleton<ICartStore>(sp => new ScriptedCartStore(ActivatorUtilities.CreateInstance<RedisCartStore>(sp)));
        }));
        var store = (ScriptedCartStore)host.Services.GetRequiredService<ICartStore>();
        var client = host.CreateClient(NoCookies);

        using var get = await CallAsync(client, HttpMethod.Get, "/api/v1/cart");
        get.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DataAsync(get)).GetProperty("lines").GetArrayLength().Should().Be(0);
        SetCookies(get).Should().BeEmpty("no cart, no cookie");
        get.Headers.Contains("ETag").Should().BeFalse();

        // Removing from, or clearing, a cart that was never made creates nothing either.
        using var remove = await CallAsync(client, HttpMethod.Delete, $"/api/v1/cart/items/{Guid.NewGuid()}");
        using var clear = await CallAsync(client, HttpMethod.Delete, "/api/v1/cart");
        using var zero = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{Guid.NewGuid()}", Qty(0));
        foreach (var r in new[] { remove, clear, zero })
        {
            r.StatusCode.Should().Be(HttpStatusCode.OK);
            SetCookies(r).Should().BeEmpty();
        }

        store.Saves.Should().Be(0);
        store.Gets.Should().Be(0, "without a valid token there is nothing to look up");
    }

    [PostgresAndRedisFact]
    public async Task TheGuestToken_IsReissuedOnEveryGuestResponse_WithHostCookieAttributes()
    {
        var v = await SeedAsync();
        var client = _factory.CreateClient(NoCookies);

        using var add = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 1));
        string cookie = CartCookie(add)!;
        AssertHostCookie(add);

        var tokens = _factory.Services.GetRequiredService<IGuestCartTokenService>();
        var guestId = tokens.Validate(cookie[(Cookie.Length + 1)..]);
        guestId.Should().NotBeNull("the cookie holds a token this server signed");

        foreach (var call in new Func<Task<HttpResponseMessage>>[]
        {
            () => CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie),
            () => CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{v.VariantId}", Qty(2), cookie),
            () => CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 1), cookie),
        })
        {
            using var response = await call();
            AssertHostCookie(response);
            tokens.Validate(CartCookie(response)![(Cookie.Length + 1)..]).Should().Be(guestId, "the same cart, with a fresh expiry");
        }
    }

    private static void AssertHostCookie(HttpResponseMessage response)
    {
        var header = SetCookies(response).Single(c => c.StartsWith(Cookie + "=", StringComparison.Ordinal));
        var parts = header.Split(';').Select(p => p.Trim().ToLowerInvariant()).ToList();
        parts.Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax").And.Contain("path=/");
        parts.Should().NotContain(p => p.StartsWith("domain", StringComparison.Ordinal), "a __Host- cookie must not carry a Domain");
        parts.Should().Contain(p => p == $"max-age={(int)ICartStore.GuestTtl.TotalSeconds}");
        header.Should().StartWith("__Host-");
    }

    [PostgresAndRedisFact]
    public async Task AForgedExpiredOrGarbageCookie_IsAnEmptyFreshGuestCart_NeverAnError()
    {
        var v = await SeedAsync();
        var (realClient, real) = await GuestWithAsync(v.VariantId, 5);
        string realToken = real[(Cookie.Length + 1)..];

        var expiredIssuer = new GuestCartTokenService(Convert.FromBase64String(DbBackedFactory.GuestCartTokenKey), null, new Clock(DateTimeOffset.UtcNow.AddDays(-8)));
        var otherKey = new GuestCartTokenService(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), null, TimeProvider.System);
        string flipped = realToken[..^2] + (realToken[^2] == 'A' ? 'B' : 'A') + realToken[^1];
        var bad = new[]
        {
            "garbage", "", new string('A', 78), new string('x', 5000), realToken + "x", flipped,
            expiredIssuer.Issue(new GuestCartId(Guid.NewGuid())), otherKey.Issue(new GuestCartId(Guid.NewGuid())),
            "\"" + realToken + "\"", "%00%00", "a=b; c=d"
        };

        foreach (string token in bad)
        {
            var client = _factory.CreateClient(NoCookies);
            string cookie = $"{Cookie}={token}";
            using var get = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
            get.StatusCode.Should().Be(HttpStatusCode.OK, $"token '{(token.Length > 20 ? token[..20] : token)}'");
            (await DataAsync(get)).GetProperty("lines").GetArrayLength().Should().Be(0);
            SetCookies(get).Should().BeEmpty();

            using var del = await CallAsync(client, HttpMethod.Delete, $"/api/v1/cart/items/{v.VariantId}", cookie: cookie);
            del.StatusCode.Should().Be(HttpStatusCode.OK);

            // A write starts a brand-new guest cart under a new id; the old cart is untouched.
            using var add = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 1), cookie);
            add.StatusCode.Should().Be(HttpStatusCode.OK);
            (await DataAsync(add)).GetProperty("totalQuantity").GetInt32().Should().Be(1);
            CartCookie(add).Should().NotBeNull().And.NotBe(real);
        }

        using var stillThere = await CallAsync(realClient, HttpMethod.Get, "/api/v1/cart", cookie: real);
        (await DataAsync(stillThere)).GetProperty("totalQuantity").GetInt32().Should().Be(5);
    }

    [PostgresAndRedisFact]
    public async Task GuestToken_IsReadOnlyFromTheHostCookie()
    {
        var v = await SeedAsync();
        var (client, cookie) = await GuestWithAsync(v.VariantId, 4);
        string token = cookie[(Cookie.Length + 1)..];

        var attempts = new (string Url, (string, string)[] Headers, string? Cookie)[]
        {
            ($"/api/v1/cart?{Cookie}={token}", [], null),
            ($"/api/v1/cart?mw_cart={token}&token={token}&guestToken={token}&guestCartId={token}", [], null),
            ("/api/v1/cart", [("X-Cart-Token", token), ("X-Guest-Cart", token), ("Authorization", $"Cart {token}")], null),
            ("/api/v1/cart", [(Cookie, token)], null),
            ("/api/v1/cart", [], $"mw_cart={token}"),                    // a name without the __Host- prefix
            ("/api/v1/cart", [], $"x{Cookie}={token}"),
        };
        foreach (var (url, headers, c) in attempts)
        {
            using var response = await CallAsync(client, HttpMethod.Get, url, cookie: c, headers: headers);
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            (await DataAsync(response)).GetProperty("lines").GetArrayLength().Should().Be(0, $"{url} must not identify the cart");
        }

        // A body field is not read either: the write lands in a new cart, not in the one the token names.
        using var viaBody = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items",
            $$"""{ "variantId": "{{v.VariantId}}", "quantity": 1, "guestToken": "{{token}}", "token": "{{token}}", "cartId": "{{token}}" }""");
        (await DataAsync(viaBody)).GetProperty("totalQuantity").GetInt32().Should().Be(1);
        CartCookie(viaBody).Should().NotBe(cookie);

        using var control = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        (await DataAsync(control)).GetProperty("totalQuantity").GetInt32().Should().Be(4);
    }

    // ---------------------------------------------------------------- round trips

    [PostgresAndRedisFact]
    public async Task Guest_AddGetSetRemove_RoundTrip()
    {
        var a = await SeedAsync(price: 100_000);
        var b = await SeedAsync(price: 40_000);
        var client = _factory.CreateClient(NoCookies);

        using var add = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(a.VariantId, 2));
        string cookie = CartCookie(add)!;
        add.Headers.GetValues("ETag").Single().Should().Be($"\"{(await DataAsync(add)).GetProperty("etag").GetString()}\"");
        (await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(a.VariantId, 3), cookie)).EnsureSuccessStatusCode();   // adds up: 5
        (await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(b.VariantId, 1), cookie)).EnsureSuccessStatusCode();

        using var get = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        var cart = await DataAsync(get);
        Line(cart, a.VariantId).GetProperty("quantity").GetInt32().Should().Be(5);
        Line(cart, a.VariantId).GetProperty("sku").GetString().Should().StartWith("SKU-");
        cart.GetProperty("lineCount").GetInt32().Should().Be(2);
        cart.GetProperty("totalQuantity").GetInt32().Should().Be(6);
        cart.GetProperty("subtotalMinor").GetInt64().Should().Be(5 * 100_000 + 40_000);
        cart.GetProperty("currency").GetString().Should().Be("BDT");

        using var set = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{a.VariantId}", Qty(2), cookie);
        Line(await DataAsync(set), a.VariantId).GetProperty("quantity").GetInt32().Should().Be(2);

        using var removed = await CallAsync(client, HttpMethod.Delete, $"/api/v1/cart/items/{a.VariantId}", cookie: cookie);
        var afterRemove = await DataAsync(removed);
        afterRemove.GetProperty("lineCount").GetInt32().Should().Be(1);
        removed.StatusCode.Should().Be(HttpStatusCode.OK);

        using var zero = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{b.VariantId}", Qty(0), cookie);
        (await DataAsync(zero)).GetProperty("lineCount").GetInt32().Should().Be(0);

        (await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(a.VariantId, 1), cookie)).EnsureSuccessStatusCode();
        using var clear = await CallAsync(client, HttpMethod.Delete, "/api/v1/cart", cookie: cookie);
        (await DataAsync(clear)).GetProperty("lineCount").GetInt32().Should().Be(0);
        using var empty = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        (await DataAsync(empty)).GetProperty("lineCount").GetInt32().Should().Be(0);
    }

    [PostgresAndRedisFact]
    public async Task SignedInUser_AddGetSetRemove_RoundTrip_AndGetsNoGuestCookie()
    {
        var a = await SeedAsync(price: 100_000);
        var (client, _) = await BuyerAsync();

        using var add = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(a.VariantId, 2));
        add.StatusCode.Should().Be(HttpStatusCode.OK);
        SetCookies(add).Should().BeEmpty("a signed-in cart is keyed by the user, not by a guest cookie");

        using var set = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{a.VariantId}", Qty(7));
        Line(await DataAsync(set), a.VariantId).GetProperty("quantity").GetInt32().Should().Be(7);
        using var get = await CallAsync(client, HttpMethod.Get, "/api/v1/cart");
        (await DataAsync(get)).GetProperty("subtotalMinor").GetInt64().Should().Be(700_000);
        using var remove = await CallAsync(client, HttpMethod.Delete, $"/api/v1/cart/items/{a.VariantId}");
        (await DataAsync(remove)).GetProperty("lineCount").GetInt32().Should().Be(0);
    }

    // ---------------------------------------------------------------- owners never mix

    [PostgresAndRedisFact]
    public async Task OneUsersCart_IsNeverSeenByAnotherUser_OrByAGuest_AndTheOwnerComesFromThePrincipal()
    {
        var v = await SeedAsync();
        var w = await SeedAsync();
        var (alice, aliceId) = await BuyerAsync();
        var (bob, bobId) = await BuyerAsync();

        // Bob names Alice in the body, the query and headers; it changes nothing: the cart is always the caller's own.
        using var addAlice = await CallAsync(alice, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 3));
        addAlice.EnsureSuccessStatusCode();
        using var addBob = await CallAsync(bob, HttpMethod.Post, $"/api/v1/cart/items?userId={aliceId}",
            $$"""{ "variantId": "{{w.VariantId}}", "quantity": 1, "userId": "{{aliceId}}", "ownerId": "{{aliceId}}", "owner": "{{aliceId}}" }""",
            headers: [("X-User-Id", aliceId.ToString())]);
        addBob.EnsureSuccessStatusCode();

        using var aliceGet = await CallAsync(alice, HttpMethod.Get, $"/api/v1/cart?userId={bobId}");
        var aliceCart = await DataAsync(aliceGet);
        aliceCart.GetProperty("lineCount").GetInt32().Should().Be(1);
        Line(aliceCart, v.VariantId).GetProperty("quantity").GetInt32().Should().Be(3);
        using var bobGet = await CallAsync(bob, HttpMethod.Get, "/api/v1/cart");
        var bobCart = await DataAsync(bobGet);
        bobCart.GetProperty("lineCount").GetInt32().Should().Be(1);
        Line(bobCart, w.VariantId).GetProperty("quantity").GetInt32().Should().Be(1);

        // A guest, with a cookie of its own cart, sees only that cart.
        var (guest, cookie) = await GuestWithAsync(w.VariantId, 9);
        using var guestGet = await CallAsync(guest, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        var guestCart = await DataAsync(guestGet);
        guestCart.GetProperty("totalQuantity").GetInt32().Should().Be(9);
        guestCart.GetProperty("lines").EnumerateArray().Should().NotContain(l => l.GetProperty("variantId").GetGuid() == v.VariantId);

        // Anonymous with no cookie sees nothing, and never someone's cart.
        using var anon = await CallAsync(_factory.CreateClient(NoCookies), HttpMethod.Get, "/api/v1/cart");
        (await DataAsync(anon)).GetProperty("lineCount").GetInt32().Should().Be(0);

        // A signed-in user who also sends a guest cookie gets the user cart, not the guest one.
        using var both = await CallAsync(alice, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        (await DataAsync(both)).GetProperty("totalQuantity").GetInt32().Should().Be(3);
    }

    // ---------------------------------------------------------------- concurrency

    [PostgresAndRedisFact]
    public async Task TwoTabsAddingAtTheSameTime_BothLand()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        var (client, cookie) = await GuestWithAsync((await SeedAsync()).VariantId);

        for (int round = 0; round < 5; round++)
        {
            var results = await Task.WhenAll(
                CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(a.VariantId, 1), cookie),
                CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(b.VariantId, 1), cookie));
            results.Select(r => r.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        }

        using var get = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        var cart = await DataAsync(get);
        Line(cart, a.VariantId).GetProperty("quantity").GetInt32().Should().Be(5);
        Line(cart, b.VariantId).GetProperty("quantity").GetInt32().Should().Be(5);
    }

    [PostgresAndRedisFact]
    public async Task AConflict_IsRetried_AndAPersistentOne_Is409AfterThreeAttempts()
    {
        var v = await SeedAsync();
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICartStore>();
            s.AddSingleton<ICartStore>(sp => new ScriptedCartStore(ActivatorUtilities.CreateInstance<RedisCartStore>(sp)));
        }));
        var store = (ScriptedCartStore)host.Services.GetRequiredService<ICartStore>();
        var client = host.CreateClient(NoCookies);

        // Two lost races: the third attempt re-reads, re-applies and saves.
        store.ConflictsToInject = 2;
        using var retried = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 2));
        retried.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DataAsync(retried)).GetProperty("totalQuantity").GetInt32().Should().Be(2, "applied once, not once per attempt");
        store.Saves.Should().Be(3);

        // Always losing: stop after three attempts with 409, and write nothing.
        store.Saves = 0;
        store.ConflictsToInject = int.MaxValue;
        using var busy = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 2));
        busy.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await busy.Content.ReadAsStringAsync()).Should().Contain("CART_BUSY");
        store.Saves.Should().Be(CartService.MaxAttempts);
        SetCookies(busy).Should().BeEmpty("no cart was created, so no token is handed out");
    }

    [PostgresAndRedisFact]
    public async Task IfMatch_RefusesAChangeToACartThatMovedOn()
    {
        var v = await SeedAsync();
        var (client, cookie) = await GuestWithAsync(v.VariantId, 1);
        using var get = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        string etag = get.Headers.GetValues("ETag").Single();

        using var stale = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{v.VariantId}", Qty(2), cookie, ("If-Match", "\"00000000000000000000000000000000\""));
        stale.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await stale.Content.ReadAsStringAsync()).Should().Contain("CART_CHANGED");

        using var fresh = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{v.VariantId}", Qty(2), cookie, ("If-Match", etag));
        fresh.StatusCode.Should().Be(HttpStatusCode.OK);
        fresh.Headers.GetValues("ETag").Single().Should().NotBe(etag);
    }

    // ---------------------------------------------------------------- limits and validation

    [PostgresAndRedisFact]
    public async Task The101stLine_IsRefused()
    {
        var variants = new List<SeededVariant>();
        for (int i = 0; i <= OrderLimits.MaxCartLines; i++) variants.Add(await SeedAsync(onHand: 10));
        var (client, userId) = await BuyerAsync();

        var store = _factory.Services.GetRequiredService<ICartStore>();
        var owner = CartOwner.ForUser(userId);
        var full = new DomainCart([.. variants.Take(OrderLimits.MaxCartLines).Select(x => new Mechware.Domain.Cart.CartItem(x.VariantId, 1, 250_000))], 1);
        (await store.SaveAsync(owner, full, null)).Status.Should().Be(CartSaveStatus.Saved);

        using var over = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(variants[^1].VariantId, 1));
        over.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await over.Content.ReadAsStringAsync()).Should().Contain("CART_FULL");

        // A line already in the cart can still change.
        using var ok = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(variants[0].VariantId, 1));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DataAsync(ok)).GetProperty("lineCount").GetInt32().Should().Be(OrderLimits.MaxCartLines);
        await store.DeleteAsync(owner);
    }

    [PostgresAndRedisFact]
    public async Task QuantityOverTheLimit_ZeroNegativeFractionalOrMissing_AreRefused()
    {
        var v = await SeedAsync(onHand: 50_000);
        var client = _factory.CreateClient(NoCookies);

        using var tooMany = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, OrderLimits.MaxQuantityPerLine + 1));
        tooMany.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var huge = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, int.MaxValue));
        huge.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        foreach (string body in new[]
        {
            $$"""{ "variantId": "{{v.VariantId}}", "quantity": 0 }""",
            $$"""{ "variantId": "{{v.VariantId}}", "quantity": -3 }""",
            $$"""{ "variantId": "{{v.VariantId}}", "quantity": 1.5 }""",
            $$"""{ "variantId": "{{v.VariantId}}", "quantity": null }""",
            $$"""{ "variantId": "{{v.VariantId}}" }""",
            $$"""{ "quantity": 1 }""",
            """{ "variantId": "not-a-guid", "quantity": 1 }""",
            "not json", "[]",
        })
        {
            using var response = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", body);
            response.StatusCode.Should().Be(HttpStatusCode.BadRequest, body);
        }

        using var negativeSet = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{v.VariantId}", Qty(-1));
        negativeSet.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var missingSet = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{v.VariantId}", "{}");
        missingSet.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A body over 4 KB is refused before it is read.
        using var big = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", $$"""{ "variantId": "{{v.VariantId}}", "quantity": 1, "pad": "{{new string('x', 5000)}}" }""");
        big.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        // The limit itself is fine.
        using var atLimit = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, OrderLimits.MaxQuantityPerLine));
        atLimit.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ---------------------------------------------------------------- sellable, stock, units

    [PostgresAndRedisFact]
    public async Task AnItemThatCannotBeSold_IsNotFound_WhateverTheReason()
    {
        var archived = await SeedAsync();
        var inactiveVariant = await SeedAsync();
        var deleted = await SeedAsync();
        var draft = await SeedAsync();
        await SqlAsync($"UPDATE products SET status = 'archived' WHERE id = {archived.ProductId}");
        await SqlAsync($"UPDATE product_variants SET status = 'inactive' WHERE id = {inactiveVariant.VariantId}");
        await SqlAsync($"UPDATE products SET deleted_at = now() WHERE id = {deleted.ProductId}");
        await SqlAsync($"UPDATE products SET status = 'draft' WHERE id = {draft.ProductId}");
        var client = _factory.CreateClient(NoCookies);

        var bodies = new List<string>();
        foreach (var id in new[] { archived.VariantId, inactiveVariant.VariantId, deleted.VariantId, draft.VariantId, Guid.NewGuid() })
        {
            using var response = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(id, 1));
            response.StatusCode.Should().Be(HttpStatusCode.NotFound);
            SetCookies(response).Should().BeEmpty("nothing was added, so no cart was made");
            using var put = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{id}", Qty(1));
            put.StatusCode.Should().Be(HttpStatusCode.NotFound);
            bodies.Add(System.Text.RegularExpressions.Regex.Replace(await response.Content.ReadAsStringAsync(), "\"(traceId|instance)\":\"[^\"]*\"", ""));
        }

        bodies.Distinct().Should().HaveCount(1, "an unknown id and an unpublished one must be indistinguishable");
    }

    [PostgresAndRedisFact]
    public async Task StockAndUnitChanges_ComeBackAsNotices()
    {
        var stock = await SeedAsync(onHand: 100);
        var gone = await SeedAsync(onHand: 100);
        var archived = await SeedAsync(onHand: 100);
        var moq = await SeedAsync(onHand: 100);
        var pack = await SeedAsync(onHand: 100);
        var (client, _) = await BuyerAsync();

        foreach (var (variant, qty) in new[] { (stock, 10), (gone, 3), (archived, 2), (moq, 4), (pack, 10) })
            (await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(variant.VariantId, qty))).EnsureSuccessStatusCode();

        await SqlAsync($"UPDATE inventory_items SET on_hand = 4 WHERE id = {stock.InventoryItemId}");
        await SqlAsync($"UPDATE inventory_items SET on_hand = 0 WHERE id = {gone.InventoryItemId}");
        await SqlAsync($"UPDATE products SET status = 'archived' WHERE id = {archived.ProductId}");
        await SqlAsync($"UPDATE product_variants SET min_order_qty = 10, pack_size = 1 WHERE id = {moq.VariantId}");
        await SqlAsync($"UPDATE product_variants SET min_order_qty = 4, pack_size = 4 WHERE id = {pack.VariantId}");

        using var get = await CallAsync(client, HttpMethod.Get, "/api/v1/cart");
        var cart = await DataAsync(get);
        NoticeKinds(cart).Should().BeEquivalentTo("stockReduced", "outOfStock", "unavailable", "belowMinimumOrder", "quantityAdjustedToPack");
        Line(cart, stock.VariantId).GetProperty("quantity").GetInt32().Should().Be(4);
        Line(cart, pack.VariantId).GetProperty("quantity").GetInt32().Should().Be(8);
        cart.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("variantId").GetGuid())
            .Should().BeEquivalentTo(new[] { stock.VariantId, pack.VariantId }, "sold-out, archived and below-minimum lines are dropped from the view");
        cart.GetProperty("notices").EnumerateArray().Single(n => n.GetProperty("kind").GetString() == "stockReduced")
            .GetProperty("variantId").GetGuid().Should().Be(stock.VariantId);

        // Adding more than is left is refused, with the number that is.
        using var tooMany = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(stock.VariantId, 1));
        tooMany.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await tooMany.Content.ReadAsStringAsync()).Should().Contain("INSUFFICIENT_STOCK");
        using var notPack = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{pack.VariantId}", Qty(6));
        notPack.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // A write stores the corrected cart, so the next read has no repeated notices for lines that were fixed.
        using var write = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{pack.VariantId}", Qty(12));
        write.StatusCode.Should().Be(HttpStatusCode.OK);
        NoticeKinds(await DataAsync(write)).Should().Contain("stockReduced");
        using var again = await CallAsync(client, HttpMethod.Get, "/api/v1/cart");
        NoticeKinds(await DataAsync(again)).Should().BeEmpty();
    }

    // ---------------------------------------------------------------- web hygiene

    [PostgresAndRedisFact]
    public async Task EveryCartResponse_IsNoStore_EvenAnError()
    {
        var v = await SeedAsync();
        var client = _factory.CreateClient(NoCookies);
        foreach (var response in new[]
        {
            await CallAsync(client, HttpMethod.Get, "/api/v1/cart"),
            await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 1)),
            await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(Guid.NewGuid(), 1)),
            await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", "{}"),
            await CallAsync(client, HttpMethod.Delete, "/api/v1/cart"),
        })
        {
            response.Headers.CacheControl!.NoStore.Should().BeTrue($"{response.StatusCode}");
            response.Dispose();
        }
    }

    [PostgresAndRedisFact]
    public async Task AGuestWrite_FromAnotherSitesOrigin_IsRefused()
    {
        var v = await SeedAsync();
        var (client, cookie) = await GuestWithAsync(v.VariantId, 1);

        foreach (var (method, url, body) in new[]
        {
            (HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 1)),
            (HttpMethod.Put, $"/api/v1/cart/items/{v.VariantId}", Qty(9)),
            (HttpMethod.Delete, $"/api/v1/cart/items/{v.VariantId}", null),
            (HttpMethod.Delete, "/api/v1/cart", null),
        })
        {
            using var evil = await CallAsync(client, method, url, body, cookie, ("Origin", "https://evil.example"));
            evil.StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{method} {url}");
        }

        using var null_ = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, 1), null, ("Origin", "null"));
        null_.StatusCode.Should().Be(HttpStatusCode.Forbidden);

        using var ours = await CallAsync(client, HttpMethod.Put, $"/api/v1/cart/items/{v.VariantId}", Qty(2), cookie, ("Origin", "http://localhost:3000"));
        ours.StatusCode.Should().Be(HttpStatusCode.OK);
        using var read = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        (await DataAsync(read)).GetProperty("totalQuantity").GetInt32().Should().Be(2, "the refused writes changed nothing");
    }
}

/// <summary>Per-IP cap on new guest carts (CT11).</summary>
public class CartGuestLimitTests : IClassFixture<CartLimitFactory>
{
    private readonly CartLimitFactory _factory;

    public CartGuestLimitTests(CartLimitFactory factory) => _factory = factory;

    [PostgresAndRedisFact]
    public async Task NewGuestCarts_ArePerIpLimited()
    {
        await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);
        var v = await CatalogTestData.SeedVariantAsync(db, 100);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        string body = $$"""{ "variantId": "{{v.VariantId}}", "quantity": 1 }""";

        string? cookie = null;
        for (int i = 0; i < 3; i++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/items") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            using var response = await client.SendAsync(request);
            response.StatusCode.Should().Be(HttpStatusCode.OK, $"cart {i + 1}");
            cookie = response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
        }

        using var fourth = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/items") { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        fourth.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        fourth.Headers.Contains("Set-Cookie").Should().BeFalse();

        // A visitor who already has a cart is not a new cart: reads and changes keep working.
        var withCookie = new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/items") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        withCookie.Headers.Add("Cookie", cookie);
        using var more = await client.SendAsync(withCookie);
        more.StatusCode.Should().Be(HttpStatusCode.OK);
        using var read = await client.SendAsync(new HttpRequestMessage(HttpMethod.Get, "/api/v1/cart"));
        read.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}

/// <summary>Redis down is "the cart is unavailable" (503), never "your cart is empty".</summary>
public class CartRedisDownTests : IClassFixture<CartRedisDownFactory>
{
    private readonly CartRedisDownFactory _factory;

    public CartRedisDownTests(CartRedisDownFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task WithoutRedis_ACartRequestIs503_NotAnEmptyCart()
    {
        await using var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);
        var v = await CatalogTestData.SeedVariantAsync(db, 100);
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });

        using var add = await client.SendAsync(new HttpRequestMessage(HttpMethod.Post, "/api/v1/cart/items")
        { Content = new StringContent($$"""{ "variantId": "{{v.VariantId}}", "quantity": 1 }""", Encoding.UTF8, "application/json") });
        add.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
        (await add.Content.ReadAsStringAsync()).Should().Contain("CART_UNAVAILABLE");
        add.Headers.Contains("Set-Cookie").Should().BeFalse();

        // A visitor with a (validly signed) cookie asking to see the cart gets 503, not an empty cart.
        var tokens = _factory.Services.GetRequiredService<IGuestCartTokenService>();
        var get = new HttpRequestMessage(HttpMethod.Get, "/api/v1/cart");
        get.Headers.Add("Cookie", $"__Host-mw_cart={tokens.Issue(GuestCartId.New())}");
        using var read = await client.SendAsync(get);
        read.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
    }
}

internal sealed class Clock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
