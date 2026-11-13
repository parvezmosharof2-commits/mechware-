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
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Cart;
using Mechware.Domain.Cart;
using Mechware.Domain.Common;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Cart;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;
using StackExchange.Redis;
using DomainCart = Mechware.Domain.Cart.Cart;

namespace Mechware.IntegrationTests.Cart;

public sealed class CartMergeFactory() : DbBackedFactory(null, new Dictionary<string, string> { ["Cart:NewGuestCartsPerIpPerHour"] = "100000" });

/// <summary>Wraps the real store: can make the merge commit fail, or run something just before it (another tab's write).</summary>
public sealed class InterceptingCartStore(ICartStore inner) : ICartStore
{
    public volatile bool FailMerges;
    public Func<Task>? BeforeNextMerge;
    public int MergeAttempts;

    public Task<StoredCart?> GetAsync(CartOwner owner, CancellationToken ct = default) => inner.GetAsync(owner, ct);

    public Task<(CartSaveStatus Status, string? ETag)> SaveAsync(CartOwner owner, DomainCart cart, string? expectedETag, CancellationToken ct = default) =>
        inner.SaveAsync(owner, cart, expectedETag, ct);

    public Task DeleteAsync(CartOwner owner, CancellationToken ct = default) => inner.DeleteAsync(owner, ct);

    public async Task<(CartSaveStatus Status, string? ETag)> SaveMergedAsync(
        CartOwner user, DomainCart merged, string? expectedUserETag, CartOwner guest, string guestETag, CancellationToken ct = default)
    {
        Interlocked.Increment(ref MergeAttempts);
        if (FailMerges) throw new ServiceUnavailableException("CART_UNAVAILABLE", "test: the cart store went away");
        var hook = Interlocked.Exchange(ref BeforeNextMerge, null);
        if (hook is not null) await hook();
        return await inner.SaveMergedAsync(user, merged, expectedUserETag, guest, guestETag, ct);
    }
}

/// <summary>
/// E1.4: the guest cart is merged into the account's cart when a session starts. Against real PostgreSQL (accounts, prices)
/// and Redis (carts). Cookies are handled by hand so a test can replay, forge or drop them.
/// </summary>
public class CartMergeTests : IClassFixture<CartMergeFactory>
{
    private const string Cookie = "__Host-mw_cart";
    private const string Password = "Buyer#Test2026xyz";

    private readonly CartMergeFactory _factory;

    public CartMergeTests(CartMergeFactory factory) => _factory = factory;

    // ---------------------------------------------------------------- helpers

    private static WebApplicationFactoryClientOptions NoCookies => new() { HandleCookies = false };

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private ICartStore Store => _factory.Services.GetRequiredService<ICartStore>();

    private IGuestCartTokenService Tokens => _factory.Services.GetRequiredService<IGuestCartTokenService>();

    private async Task<SeededVariant> SeedAsync(int onHand = 100, long price = 100_000)
    {
        await using var db = Db();
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
    }

    private async Task<(string Email, Guid UserId)> AccountAsync()
    {
        string email = $"buyer.{Guid.NewGuid():N}@example.test";
        await using var db = Db();
        var user = User.Create(email, null, new PasswordHasherService().HashPassword(Password), "Test Buyer", "en");
        user.VerifyEmail(DateTime.UtcNow);
        user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return (email, user.Id);
    }

    /// <summary>Each login comes from its own address, so the per-IP sign-in limit never interferes.</summary>
    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string email, string? cookie)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { identifier = email, password = Password }) };
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        request.Headers.TryAddWithoutValidation("X-Forwarded-For", $"10.{Random.Shared.Next(256)}.{Random.Shared.Next(256)}.{Random.Shared.Next(1, 255)}");
        return await client.SendAsync(request);
    }

    private static async Task<HttpResponseMessage> CallAsync(HttpClient client, HttpMethod method, string url, string? body = null, string? cookie = null, string? bearer = null)
    {
        var request = new HttpRequestMessage(method, url);
        if (body is not null) request.Content = new StringContent(body, Encoding.UTF8, "application/json");
        if (cookie is not null) request.Headers.Add("Cookie", cookie);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        return await client.SendAsync(request);
    }

    private static string Add(Guid variantId, int quantity) => $$"""{ "variantId": "{{variantId}}", "quantity": {{quantity}} }""";

    private static List<string> SetCookies(HttpResponseMessage response) =>
        response.Headers.TryGetValues("Set-Cookie", out var values) ? [.. values] : [];

    private static string? CartCookie(HttpResponseMessage response) =>
        SetCookies(response).FirstOrDefault(c => c.StartsWith(Cookie + "=", StringComparison.Ordinal))?.Split(';')[0];

    private static async Task<JsonElement> DataAsync(HttpResponseMessage response) =>
        (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");

    private static Dictionary<Guid, int> Quantities(DomainCart cart) => cart.Items.ToDictionary(i => i.VariantId, i => i.Quantity);

    private GuestCartId GuestIdOf(string cookie) => Tokens.Validate(cookie[(Cookie.Length + 1)..]) ?? throw new InvalidOperationException("not a valid cookie");

    /// <summary>A guest who added these lines through the API; returns the cart cookie ("name=value").</summary>
    private async Task<string> GuestAsync(params (Guid Variant, int Quantity)[] lines)
    {
        var client = _factory.CreateClient(NoCookies);
        string? cookie = null;
        foreach (var (variant, quantity) in lines)
        {
            using var added = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(variant, quantity), cookie);
            added.StatusCode.Should().Be(HttpStatusCode.OK);
            cookie = CartCookie(added);
        }
        return cookie!;
    }

    /// <summary>The response expires the guest cookie, with the attributes a <c>__Host-</c> cookie needs to be replaced.</summary>
    private static void ShouldExpireTheGuestCookie(HttpResponseMessage response)
    {
        var header = SetCookies(response).Should().ContainSingle(c => c.StartsWith(Cookie + "=", StringComparison.Ordinal)).Subject;
        header.Should().StartWith(Cookie + "=;", "the value is emptied");
        var parts = header.Split(';').Select(p => p.Trim()).ToList();
        parts.Select(p => p.ToLowerInvariant()).Should().Contain("httponly").And.Contain("secure").And.Contain("samesite=lax").And.Contain("path=/");
        parts.Should().NotContain(p => p.StartsWith("domain", StringComparison.OrdinalIgnoreCase));
        var expires = parts.Single(p => p.StartsWith("expires=", StringComparison.OrdinalIgnoreCase))["expires=".Length..];
        DateTimeOffset.Parse(expires, System.Globalization.CultureInfo.InvariantCulture).Should().BeBefore(DateTimeOffset.UtcNow);
    }

    // ---------------------------------------------------------------- CT10: merge, then the guest cart is gone

    [PostgresAndRedisFact]
    public async Task AfterMerge_TheGuestCartIsGone_AndReplayingItsTokenShowsAnEmptyCart()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        string cookie = await GuestAsync((a.VariantId, 2), (b.VariantId, 1));
        var guestId = GuestIdOf(cookie);
        var (email, userId) = await AccountAsync();
        var client = _factory.CreateClient(NoCookies);

        using var login = await LoginAsync(client, email, cookie);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldExpireTheGuestCookie(login);
        var data = await DataAsync(login);
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("cartMerged").GetProperty("lineCount").GetInt32().Should().Be(2);
        data.GetProperty("cartMerged").GetProperty("notices").GetArrayLength().Should().Be(0);

        (await Store.GetAsync(CartOwner.FromValidatedGuestToken(guestId))).Should().BeNull("the merged guest cart is deleted");
        string bearer = data.GetProperty("accessToken").GetString()!;
        using var mine = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", bearer: bearer);
        var cart = await DataAsync(mine);
        cart.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("variantId").GetGuid(), l.GetProperty("quantity").GetInt32()))
            .Should().Equal((a.VariantId, 2), (b.VariantId, 1));

        // Replaying the old token (a copied cookie, a second tab that missed the expiry) finds nothing and resurrects nothing.
        var stranger = _factory.CreateClient(NoCookies);
        using var replay = await CallAsync(stranger, HttpMethod.Get, "/api/v1/cart", cookie: cookie);
        replay.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DataAsync(replay)).GetProperty("lines").GetArrayLength().Should().Be(0);
        SetCookies(replay).Should().BeEmpty("no cart, so no token is handed back");
        Quantities((await Store.GetAsync(CartOwner.ForUser(userId)))!.Cart).Should().BeEquivalentTo(new Dictionary<Guid, int> { [a.VariantId] = 2, [b.VariantId] = 1 });
    }

    [PostgresAndRedisFact]
    public async Task Logout_StartsANewGuestId()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        string before = await GuestAsync((a.VariantId, 1));
        var (email, _) = await AccountAsync();
        var client = _factory.CreateClient(NoCookies);
        using var login = await LoginAsync(client, email, before);
        string bearer = (await DataAsync(login)).GetProperty("accessToken").GetString()!;
        string refresh = SetCookies(login).Single(c => c.StartsWith("refresh_token=", StringComparison.Ordinal)).Split(';')[0];

        // Logout expires the guest cookie too, whether or not the browser still sends one.
        using var logout = await CallAsync(client, HttpMethod.Post, "/api/v1/auth/logout", cookie: $"{refresh}; {before}", bearer: bearer);
        logout.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldExpireTheGuestCookie(logout);

        // The next guest cart in this browser is a new one, under a new id, holding only what is added now.
        using var next = await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(b.VariantId, 3));
        string after = CartCookie(next)!;
        GuestIdOf(after).Should().NotBe(GuestIdOf(before));
        var cart = await DataAsync(next);
        cart.GetProperty("lines").EnumerateArray().Should().ContainSingle().Which.GetProperty("variantId").GetGuid().Should().Be(b.VariantId);
        using var old = await CallAsync(client, HttpMethod.Get, "/api/v1/cart", cookie: before);
        (await DataAsync(old)).GetProperty("lines").GetArrayLength().Should().Be(0, "the pre-login cart was merged away and does not come back");
    }

    // ---------------------------------------------------------------- merge rules (CartEngine.Merge)

    [PostgresAndRedisFact]
    public async Task Login_MergesIntoTheUsersCart_AddingSharedLines_CappingAtTheLineMaximum_AndSaysSo()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        var c = await SeedAsync();
        var d = await SeedAsync(onHand: 20_000);
        var (email, userId) = await AccountAsync();
        var client = _factory.CreateClient(NoCookies);
        using var first = await LoginAsync(client, email, null);
        string bearer = (await DataAsync(first)).GetProperty("accessToken").GetString()!;
        foreach (var (v, q) in new[] { (a, 3), (b, 1), (d, 9_000) })
            (await CallAsync(client, HttpMethod.Post, "/api/v1/cart/items", Add(v.VariantId, q), bearer: bearer)).EnsureSuccessStatusCode();

        string cookie = await GuestAsync((c.VariantId, 2), (a.VariantId, 2), (d.VariantId, 2_000));

        using var login = await LoginAsync(client, email, cookie);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var merged = (await DataAsync(login)).GetProperty("cartMerged");
        merged.GetProperty("lineCount").GetInt32().Should().Be(4);
        var notice = merged.GetProperty("notices").EnumerateArray().Should().ContainSingle().Subject;
        notice.GetProperty("kind").GetString().Should().Be("quantityCapped");
        notice.GetProperty("variantId").GetGuid().Should().Be(d.VariantId);
        notice.GetProperty("was").GetInt64().Should().Be(11_000);
        notice.GetProperty("now").GetInt64().Should().Be(OrderLimits.MaxQuantityPerLine);

        // The buyer's own lines first, in their order, then the guest's new ones.
        var stored = (await Store.GetAsync(CartOwner.ForUser(userId)))!.Cart;
        stored.Items.Select(i => (i.VariantId, i.Quantity)).Should().Equal(
            (a.VariantId, 5), (b.VariantId, 1), (d.VariantId, OrderLimits.MaxQuantityPerLine), (c.VariantId, 2));
    }

    [PostgresAndRedisFact]
    public async Task Login_IntoAFullCart_KeepsTheHundredLineLimit_AndNamesEachGuestLineLeftOut()
    {
        var (email, userId) = await AccountAsync();
        var user = CartOwner.ForUser(userId);
        var full = new DomainCart([.. Enumerable.Range(0, OrderLimits.MaxCartLines).Select(_ => new CartItem(Guid.NewGuid(), 1, 100))], 1);
        (await Store.SaveAsync(user, full, null)).Status.Should().Be(CartSaveStatus.Saved);
        var guestId = GuestCartId.New();
        Guid shared = full.Items[7].VariantId, extra1 = Guid.NewGuid(), extra2 = Guid.NewGuid();
        var guestCart = new DomainCart([new CartItem(shared, 4, 100), new CartItem(extra1, 2, 100), new CartItem(extra2, 3, 100)], 1);
        (await Store.SaveAsync(CartOwner.FromValidatedGuestToken(guestId), guestCart, null)).Status.Should().Be(CartSaveStatus.Saved);

        using var login = await LoginAsync(_factory.CreateClient(NoCookies), email, $"{Cookie}={Tokens.Issue(guestId)}");
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var merged = (await DataAsync(login)).GetProperty("cartMerged");
        merged.GetProperty("lineCount").GetInt32().Should().Be(OrderLimits.MaxCartLines);
        merged.GetProperty("notices").EnumerateArray()
            .Select(n => (n.GetProperty("kind").GetString(), n.GetProperty("variantId").GetGuid(), n.GetProperty("was").GetInt64()))
            .Should().Equal(("cartFull", extra1, 2L), ("cartFull", extra2, 3L));

        var stored = (await Store.GetAsync(user))!.Cart;
        stored.Items.Should().HaveCount(OrderLimits.MaxCartLines);
        stored.Items.Single(i => i.VariantId == shared).Quantity.Should().Be(5, "a line already in the cart still grows");
        (await Store.GetAsync(CartOwner.FromValidatedGuestToken(guestId))).Should().BeNull();
        await Store.DeleteAsync(user);
    }

    [PostgresAndRedisFact]
    public async Task Refresh_AlsoTakesOverTheGuestCart()
    {
        // The web restores a session with /auth/refresh on page load; a cart started as a guest in between is not stranded.
        var a = await SeedAsync();
        var (email, userId) = await AccountAsync();
        var client = _factory.CreateClient(NoCookies);
        using var login = await LoginAsync(client, email, null);
        string refresh = SetCookies(login).Single(c => c.StartsWith("refresh_token=", StringComparison.Ordinal)).Split(';')[0];
        string cookie = await GuestAsync((a.VariantId, 4));

        using var refreshed = await CallAsync(client, HttpMethod.Post, "/api/v1/auth/refresh", cookie: $"{refresh}; {cookie}");
        refreshed.StatusCode.Should().Be(HttpStatusCode.OK);
        ShouldExpireTheGuestCookie(refreshed);
        (await DataAsync(refreshed)).GetProperty("cartMerged").GetProperty("lineCount").GetInt32().Should().Be(1);
        Quantities((await Store.GetAsync(CartOwner.ForUser(userId)))!.Cart).Should().BeEquivalentTo(new Dictionary<Guid, int> { [a.VariantId] = 4 });
    }

    [PostgresAndRedisFact]
    public async Task Login_WithoutAGuestCookie_ChangesNoCart_AndSetsNoCartCookie()
    {
        var (email, userId) = await AccountAsync();
        using var login = await LoginAsync(_factory.CreateClient(NoCookies), email, null);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DataAsync(login)).GetProperty("cartMerged").ValueKind.Should().Be(JsonValueKind.Null);
        CartCookie(login).Should().BeNull();
        (await Store.GetAsync(CartOwner.ForUser(userId))).Should().BeNull();
    }

    // ---------------------------------------------------------------- never twice, never lost

    [PostgresAndRedisFact]
    public async Task AMergeWhoseResponseWasLost_IsNotAppliedAgain_OnTheNextLogin()
    {
        // The merge committed, but the browser never saw the cookie expire (crash, dropped connection) and logs in again with it.
        var a = await SeedAsync();
        string cookie = await GuestAsync((a.VariantId, 3));
        var (email, userId) = await AccountAsync();

        using var first = await LoginAsync(_factory.CreateClient(NoCookies), email, cookie);
        (await DataAsync(first)).GetProperty("cartMerged").GetProperty("lineCount").GetInt32().Should().Be(1);
        using var again = await LoginAsync(_factory.CreateClient(NoCookies), email, cookie);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
        (await DataAsync(again)).GetProperty("cartMerged").ValueKind.Should().Be(JsonValueKind.Null, "the guest cart no longer exists");
        ShouldExpireTheGuestCookie(again);

        Quantities((await Store.GetAsync(CartOwner.ForUser(userId)))!.Cart)[a.VariantId].Should().Be(3, "merged once, not twice");
    }

    [PostgresAndRedisFact]
    public async Task LoginStillSucceeds_WhenTheMergeFails_AndTheGuestCartIsKept_ForTheNextLogin()
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICartStore>();
            s.AddSingleton<ICartStore>(sp => new InterceptingCartStore(ActivatorUtilities.CreateInstance<RedisCartStore>(sp)));
        }));
        var store = (InterceptingCartStore)host.Services.GetRequiredService<ICartStore>();
        var a = await SeedAsync();
        string cookie = await GuestAsync((a.VariantId, 2));
        var guest = CartOwner.FromValidatedGuestToken(GuestIdOf(cookie));
        var before = await Store.GetAsync(guest);
        var (email, userId) = await AccountAsync();

        store.FailMerges = true;
        using var login = await LoginAsync(host.CreateClient(NoCookies), email, cookie);
        login.StatusCode.Should().Be(HttpStatusCode.OK, "a cart problem never fails a sign-in");
        var data = await DataAsync(login);
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("cartMerged").ValueKind.Should().Be(JsonValueKind.Null);
        CartCookie(login).Should().BeNull("the guest cookie is kept, so the next sign-in can merge it");
        store.MergeAttempts.Should().Be(1);

        var after = await Store.GetAsync(guest);
        after!.ETag.Should().Be(before!.ETag, "the guest cart is untouched");
        Quantities(after.Cart).Should().BeEquivalentTo(Quantities(before.Cart));
        (await Store.GetAsync(CartOwner.ForUser(userId))).Should().BeNull();

        store.FailMerges = false;
        using var retry = await LoginAsync(host.CreateClient(NoCookies), email, cookie);
        ShouldExpireTheGuestCookie(retry);
        Quantities((await Store.GetAsync(CartOwner.ForUser(userId)))!.Cart).Should().BeEquivalentTo(new Dictionary<Guid, int> { [a.VariantId] = 2 });
        (await Store.GetAsync(guest)).Should().BeNull();
    }

    [PostgresAndRedisFact]
    public async Task AGuestAddInAnotherTab_RightBeforeTheMergeCommits_IsMergedToo_NotLost()
    {
        var host = _factory.WithWebHostBuilder(b => b.ConfigureTestServices(s =>
        {
            s.RemoveAll<ICartStore>();
            s.AddSingleton<ICartStore>(sp => new InterceptingCartStore(ActivatorUtilities.CreateInstance<RedisCartStore>(sp)));
        }));
        var store = (InterceptingCartStore)host.Services.GetRequiredService<ICartStore>();
        var a = await SeedAsync();
        var b = await SeedAsync();
        string cookie = await GuestAsync((a.VariantId, 2));
        var (email, userId) = await AccountAsync();

        // Between the merge reading the guest cart and committing, the other tab adds an item (through the API, same cookie).
        store.BeforeNextMerge = async () =>
        {
            using var tab = await CallAsync(_factory.CreateClient(NoCookies), HttpMethod.Post, "/api/v1/cart/items", Add(b.VariantId, 5), cookie);
            tab.StatusCode.Should().Be(HttpStatusCode.OK);
        };

        using var login = await LoginAsync(host.CreateClient(NoCookies), email, cookie);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        store.MergeAttempts.Should().Be(2, "the first commit saw a changed guest cart and wrote nothing; the retry merged the new version");
        Quantities((await Store.GetAsync(CartOwner.ForUser(userId)))!.Cart)
            .Should().BeEquivalentTo(new Dictionary<Guid, int> { [a.VariantId] = 2, [b.VariantId] = 5 });
        (await Store.GetAsync(CartOwner.FromValidatedGuestToken(GuestIdOf(cookie)))).Should().BeNull();
    }

    [PostgresAndRedisFact]
    public async Task AGuestAddRacingTheLogin_EndsUpExactlyOnce_InTheUserCartOrTheGuestCart()
    {
        var a = await SeedAsync();
        var b = await SeedAsync();
        for (int round = 0; round < 6; round++)
        {
            string cookie = await GuestAsync((a.VariantId, 1));
            var guest = CartOwner.FromValidatedGuestToken(GuestIdOf(cookie));
            var (email, userId) = await AccountAsync();

            var login = LoginAsync(_factory.CreateClient(NoCookies), email, cookie);
            var add = CallAsync(_factory.CreateClient(NoCookies), HttpMethod.Post, "/api/v1/cart/items", Add(b.VariantId, 1), cookie);
            await Task.WhenAll(login, add);
            (await login).StatusCode.Should().Be(HttpStatusCode.OK);
            (await add).StatusCode.Should().Be(HttpStatusCode.OK);

            var userCart = (await Store.GetAsync(CartOwner.ForUser(userId)))?.Cart ?? DomainCart.Empty;
            var guestCart = (await Store.GetAsync(guest))?.Cart ?? DomainCart.Empty;
            int bTotal = userCart.Items.Concat(guestCart.Items).Where(i => i.VariantId == b.VariantId).Sum(i => i.Quantity);
            bTotal.Should().Be(1, $"round {round}: the racing add is kept exactly once");
            userCart.Items.Single(i => i.VariantId == a.VariantId).Quantity.Should().Be(1, $"round {round}: merged exactly once");
            guestCart.Items.Should().NotContain(i => i.VariantId == a.VariantId, "a merged line does not stay behind in the guest cart");
            await Store.DeleteAsync(guest);
        }
    }

    [PostgresAndRedisFact]
    public async Task ParallelLogins_WithTheSameGuestCookie_MergeItExactlyOnce()
    {
        var a = await SeedAsync();
        string cookie = await GuestAsync((a.VariantId, 3));
        var (email, userId) = await AccountAsync();

        var logins = await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => LoginAsync(_factory.CreateClient(NoCookies), email, cookie)));

        logins.Select(l => l.StatusCode).Should().AllBeEquivalentTo(HttpStatusCode.OK);
        int merges = 0;
        foreach (var login in logins)
            if ((await DataAsync(login)).GetProperty("cartMerged").ValueKind == JsonValueKind.Object) merges++;
        merges.Should().Be(1);
        Quantities((await Store.GetAsync(CartOwner.ForUser(userId)))!.Cart)[a.VariantId].Should().Be(3);
    }

    // ---------------------------------------------------------------- the cookie is the only proof

    [PostgresAndRedisFact]
    public async Task AForgedOrExpiredGuestCookie_AtLogin_IsIgnored_WithoutAnError()
    {
        var a = await SeedAsync();
        string real = await GuestAsync((a.VariantId, 2));
        string token = real[(Cookie.Length + 1)..];
        var guestId = GuestIdOf(real);
        var before = await Store.GetAsync(CartOwner.FromValidatedGuestToken(guestId));
        var expiredIssuer = new GuestCartTokenService(Convert.FromBase64String(DbBackedFactory.GuestCartTokenKey), null, new Clock(DateTimeOffset.UtcNow.AddDays(-8)));
        var otherKey = new GuestCartTokenService(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32), null, TimeProvider.System);

        foreach (string bad in new[]
        {
            "garbage", "", token[..^2] + (token[^2] == 'A' ? 'B' : 'A') + token[^1], token + "x",
            expiredIssuer.Issue(guestId),            // the right id, signed with our key, but expired
            otherKey.Issue(guestId),                 // the right id, someone else's signature
            guestId.Value.ToString(), guestId.Value.ToString("N"),   // knowing the id is not enough
        })
        {
            var (email, userId) = await AccountAsync();
            using var login = await LoginAsync(_factory.CreateClient(NoCookies), email, $"{Cookie}={bad}");
            login.StatusCode.Should().Be(HttpStatusCode.OK, $"cookie '{(bad.Length > 12 ? bad[..12] : bad)}'");
            (await DataAsync(login)).GetProperty("cartMerged").ValueKind.Should().Be(JsonValueKind.Null);
            (await Store.GetAsync(CartOwner.ForUser(userId))).Should().BeNull("nothing was merged into this account");
        }

        var after = await Store.GetAsync(CartOwner.FromValidatedGuestToken(guestId));
        after!.ETag.Should().Be(before!.ETag, "the real owner's guest cart was never touched");
    }
}

/// <summary>Redis is down: signing in still works; the merge is skipped and the guest cookie is kept for a later try.</summary>
public class CartMergeRedisDownTests : IClassFixture<CartRedisDownFactory>
{
    private readonly CartRedisDownFactory _factory;

    public CartMergeRedisDownTests(CartRedisDownFactory factory) => _factory = factory;

    [PostgresFact]
    public async Task LoginStillSucceeds_WhenRedisIsDown_AndKeepsTheGuestCookie()
    {
        string email = $"buyer.{Guid.NewGuid():N}@example.test";
        const string password = "Buyer#Test2026xyz";
        await using (var db = new MechwareDbContext(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options))
        {
            var user = User.Create(email, null, new PasswordHasherService().HashPassword(password), "Test Buyer", "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }

        var tokens = _factory.Services.GetRequiredService<IGuestCartTokenService>();
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { HandleCookies = false });
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/auth/login") { Content = JsonContent.Create(new { identifier = email, password }) };
        request.Headers.Add("Cookie", $"__Host-mw_cart={tokens.Issue(GuestCartId.New())}");
        using var login = await client.SendAsync(request);

        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("accessToken").GetString().Should().NotBeNullOrEmpty();
        data.GetProperty("cartMerged").ValueKind.Should().Be(JsonValueKind.Null);
        (login.Headers.TryGetValues("Set-Cookie", out var cookies) ? cookies : []).Should().NotContain(c => c.StartsWith("__Host-mw_cart=", StringComparison.Ordinal));
    }
}

/// <summary>The atomic merge commit (<see cref="ICartStore.SaveMergedAsync"/>) against a real Redis.</summary>
public class RedisCartMergeStoreTests : IAsyncLifetime
{
    private static readonly Lazy<ConnectionMultiplexer> Redis = new(() => ConnectionMultiplexer.Connect(TestInfra.RedisConnection));
    private readonly List<string> _keys = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_keys.Count > 0) await Redis.Value.GetDatabase().KeyDeleteAsync([.. _keys.Select(k => (RedisKey)k)]);
    }

    private static RedisCartStore Store() => new(Redis.Value, NullLogger<RedisCartStore>.Instance);

    private CartOwner User() => Track(CartOwner.ForUser(Guid.NewGuid()));

    private CartOwner Guest() => Track(CartOwner.FromValidatedGuestToken(GuestCartId.New()));

    private CartOwner Track(CartOwner owner)
    {
        _keys.Add(owner.StoreKey);
        return owner;
    }

    private static DomainCart CartWith(Guid variant, int quantity) => new([new CartItem(variant, quantity, 100)], 1);

    [RedisFact]
    public async Task SaveMerged_SavesTheUserCart_AndDeletesTheGuestCart_InOneStep()
    {
        var store = Store();
        var (user, guest) = (User(), Guest());
        var (_, guestETag) = await store.SaveAsync(guest, CartWith(Guid.NewGuid(), 2), null);
        var merged = CartWith(Guid.NewGuid(), 2);

        var (status, etag) = await store.SaveMergedAsync(user, merged, null, guest, guestETag!);

        status.Should().Be(CartSaveStatus.Saved);
        var stored = await store.GetAsync(user);
        stored!.ETag.Should().Be(etag);
        stored.Cart.Items.Should().Equal(merged.Items);
        (await store.GetAsync(guest)).Should().BeNull();
        (await Redis.Value.GetDatabase().KeyTimeToLiveAsync(user.StoreKey)).Should().BeCloseTo(ICartStore.UserTtl, TimeSpan.FromMinutes(1));
    }

    [RedisFact]
    public async Task SaveMerged_WhenTheGuestCartChangedSinceItWasRead_WritesNothing()
    {
        var store = Store();
        var (user, guest) = (User(), Guest());
        var (_, userETag) = await store.SaveAsync(user, CartWith(Guid.NewGuid(), 1), null);
        var (_, readETag) = await store.SaveAsync(guest, CartWith(Guid.NewGuid(), 2), null);
        var (_, newerETag) = await store.SaveAsync(guest, CartWith(Guid.NewGuid(), 7), readETag);   // another tab

        var (status, _) = await store.SaveMergedAsync(user, CartWith(Guid.NewGuid(), 9), userETag, guest, readETag!);

        status.Should().Be(CartSaveStatus.Conflict);
        (await store.GetAsync(user))!.ETag.Should().Be(userETag, "the user cart is not saved without the delete");
        (await store.GetAsync(guest))!.ETag.Should().Be(newerETag, "the newer guest cart is kept, to be merged by the retry");
    }

    [RedisFact]
    public async Task SaveMerged_WhenTheUserCartChanged_WritesNothing_AndKeepsTheGuestCart()
    {
        var store = Store();
        var (user, guest) = (User(), Guest());
        var (_, stale) = await store.SaveAsync(user, CartWith(Guid.NewGuid(), 1), null);
        var (_, current) = await store.SaveAsync(user, CartWith(Guid.NewGuid(), 2), stale);
        var (_, guestETag) = await store.SaveAsync(guest, CartWith(Guid.NewGuid(), 2), null);

        (await store.SaveMergedAsync(user, CartWith(Guid.NewGuid(), 9), stale, guest, guestETag!)).Status.Should().Be(CartSaveStatus.Conflict);
        (await store.SaveMergedAsync(user, CartWith(Guid.NewGuid(), 9), null, guest, guestETag!)).Status.Should().Be(CartSaveStatus.Conflict, "null = the user cart must not exist");

        (await store.GetAsync(user))!.ETag.Should().Be(current);
        (await store.GetAsync(guest))!.ETag.Should().Be(guestETag);
    }

    [RedisFact]
    public async Task SaveMerged_WhenTheGuestCartIsAlreadyGone_WritesNothing()
    {
        var store = Store();
        var (user, guest) = (User(), Guest());
        var (_, guestETag) = await store.SaveAsync(guest, CartWith(Guid.NewGuid(), 2), null);
        await store.DeleteAsync(guest);   // merged by another login, or expired

        (await store.SaveMergedAsync(user, CartWith(Guid.NewGuid(), 2), null, guest, guestETag!)).Status.Should().Be(CartSaveStatus.Conflict);
        (await store.GetAsync(user)).Should().BeNull();
    }

    [RedisFact]
    public async Task ConcurrentMerges_OfOneGuestCart_ExactlyOneCommits()
    {
        var store = Store();
        var guest = Guest();
        var users = Enumerable.Range(0, 20).Select(_ => User()).ToList();
        var (_, guestETag) = await store.SaveAsync(guest, CartWith(Guid.NewGuid(), 2), null);

        var results = await Task.WhenAll(users.Select(u => store.SaveMergedAsync(u, CartWith(Guid.NewGuid(), 2), null, guest, guestETag!)));

        results.Count(r => r.Status == CartSaveStatus.Saved).Should().Be(1);
        int owners = 0;
        foreach (var u in users) if (await store.GetAsync(u) is not null) owners++;
        owners.Should().Be(1, "only the winner received the guest's lines");
    }

    [RedisFact]
    public async Task SaveMerged_RefusesSwappedOwners_AndMalformedETags()
    {
        var store = Store();
        var (user, guest) = (User(), Guest());
        var (_, guestETag) = await store.SaveAsync(guest, CartWith(Guid.NewGuid(), 2), null);

        Func<Task> swapped = () => store.SaveMergedAsync(guest, CartWith(Guid.NewGuid(), 2), null, user, guestETag!);
        await swapped.Should().ThrowAsync<ArgumentException>();
        Func<Task> twoUsers = () => store.SaveMergedAsync(user, CartWith(Guid.NewGuid(), 2), null, User(), guestETag!);
        await twoUsers.Should().ThrowAsync<ArgumentException>();
        (await store.SaveMergedAsync(user, CartWith(Guid.NewGuid(), 2), null, guest, "*")).Status.Should().Be(CartSaveStatus.Conflict);
        (await store.SaveMergedAsync(user, CartWith(Guid.NewGuid(), 2), "nope", guest, guestETag!)).Status.Should().Be(CartSaveStatus.Conflict);
        (await store.GetAsync(guest))!.ETag.Should().Be(guestETag);
        (await store.GetAsync(user)).Should().BeNull();
    }
}
