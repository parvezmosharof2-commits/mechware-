using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Cart;
using Mechware.Domain.Cart;
using Mechware.Domain.Common.Exceptions;
using Mechware.Infrastructure.Cart;
using Mechware.IntegrationTests.Support;
using StackExchange.Redis;
using DomainCart = Mechware.Domain.Cart.Cart;

namespace Mechware.IntegrationTests.Cart;

/// <summary>[Fact] that skips when no local Redis is reachable (CI provides one).</summary>
public sealed class RedisFactAttribute : FactAttribute
{
    public RedisFactAttribute()
    {
        if (!TestInfra.RedisAvailable) Skip = "Redis not reachable (set MECHWARE_TEST_REDIS).";
    }
}

public sealed class RedisTheoryAttribute : TheoryAttribute
{
    public RedisTheoryAttribute()
    {
        if (!TestInfra.RedisAvailable) Skip = "Redis not reachable (set MECHWARE_TEST_REDIS).";
    }
}

/// <summary>
/// E1.2: the cart store over a real Redis. Compare-and-set under concurrency, TTLs, distrust of stored data, and fail-closed
/// behaviour when Redis is down. Every test uses fresh random owners and removes its keys afterwards.
/// </summary>
public class RedisCartStoreTests : IAsyncLifetime
{
    private static readonly Lazy<ConnectionMultiplexer> Redis = new(() => ConnectionMultiplexer.Connect(TestInfra.RedisConnection));
    private static readonly Lazy<ConnectionMultiplexer> SecondInstance = new(() => ConnectionMultiplexer.Connect(TestInfra.RedisConnection));
    private readonly List<string> _keys = [];

    public Task InitializeAsync() => Task.CompletedTask;

    public async Task DisposeAsync()
    {
        if (_keys.Count > 0) await Redis.Value.GetDatabase().KeyDeleteAsync([.. _keys.Select(k => (RedisKey)k)]);
    }

    private static RedisCartStore Store(IConnectionMultiplexer? redis = null) => new(redis ?? Redis.Value, NullLogger<RedisCartStore>.Instance);

    private static IDatabase Db => Redis.Value.GetDatabase();

    private CartOwner User() => Track(CartOwner.ForUser(Guid.NewGuid()));

    private CartOwner Guest() => Track(CartOwner.FromValidatedGuestToken(GuestCartId.New()));

    private CartOwner Track(CartOwner owner)
    {
        _keys.Add(owner.StoreKey);
        return owner;
    }

    private static DomainCart CartWith(Guid variant, int quantity = 2, long price = 12_500, int version = 1) =>
        new([new CartItem(variant, quantity, price)], version);

    private static DomainCart ACart() => CartWith(Guid.NewGuid());

    private static void ShouldMatch(DomainCart actual, DomainCart expected)
    {
        actual.Version.Should().Be(expected.Version);
        actual.Items.Should().Equal(expected.Items);
    }

    [RedisFact]
    public async Task SaveThenGet_RoundTripsTheCart_WithTheReturnedETag()
    {
        var store = Store();
        var owner = Guest();
        var cart = new DomainCart([new CartItem(Guid.NewGuid(), 3, 150_000), new CartItem(Guid.NewGuid(), 10_000, 0)], 4);

        (await store.GetAsync(owner)).Should().BeNull();
        var (status, etag) = await store.SaveAsync(owner, cart, null);
        status.Should().Be(CartSaveStatus.Saved);
        etag.Should().MatchRegex("^[0-9a-f]{32}$");

        var stored = await store.GetAsync(owner);
        stored!.ETag.Should().Be(etag);
        ShouldMatch(stored.Cart, cart);
    }

    [RedisFact]
    public async Task Save_WithAStaleETag_Conflicts_AndWritesNothing()
    {
        var store = Store();
        var owner = User();
        var first = ACart();
        var (_, e1) = await store.SaveAsync(owner, first, null);
        var second = ACart();
        var (status2, e2) = await store.SaveAsync(owner, second, e1);
        status2.Should().Be(CartSaveStatus.Saved);
        e2.Should().NotBe(e1);

        await Db.KeyExpireAsync(owner.StoreKey, TimeSpan.FromSeconds(120));
        var (stale, none) = await store.SaveAsync(owner, ACart(), e1);
        (stale, none).Should().Be((CartSaveStatus.Conflict, (string?)null));

        var stored = await store.GetAsync(owner);
        stored!.ETag.Should().Be(e2);
        ShouldMatch(stored.Cart, second);
    }

    [RedisFact]
    public async Task AConflict_DoesNotRefreshTheTtl()
    {
        var store = Store();
        var owner = Guest();
        var (_, e1) = await store.SaveAsync(owner, ACart(), null);
        await store.SaveAsync(owner, ACart(), e1);
        await Db.KeyExpireAsync(owner.StoreKey, TimeSpan.FromSeconds(120));

        (await store.SaveAsync(owner, ACart(), e1)).Status.Should().Be(CartSaveStatus.Conflict);
        (await store.SaveAsync(owner, ACart(), null)).Status.Should().Be(CartSaveStatus.Conflict);
        (await Db.KeyTimeToLiveAsync(owner.StoreKey))!.Value.Should().BeLessThanOrEqualTo(TimeSpan.FromSeconds(120));
    }

    [RedisFact]
    public async Task Save_WithNoExpectedETag_WhenTheCartExists_Conflicts()
    {
        var store = Store();
        var owner = Guest();
        var original = ACart();
        await store.SaveAsync(owner, original, null);

        (await store.SaveAsync(owner, ACart(), null)).Should().Be((CartSaveStatus.Conflict, (string?)null));
        ShouldMatch((await store.GetAsync(owner))!.Cart, original);
    }

    [RedisFact]
    public async Task FiftyConcurrentWriters_WithTheSameETag_ExactlyOneSaves()
    {
        var owner = User();
        var (_, etag) = await Store().SaveAsync(owner, ACart(), null);
        var stores = new[] { Store(), Store(SecondInstance.Value) };   // two "API instances"
        var carts = Enumerable.Range(0, 50).Select(_ => ACart()).ToArray();
        using var start = new ManualResetEventSlim();

        var writers = carts.Select((cart, i) => Task.Run(async () =>
        {
            start.Wait();
            return (Cart: cart, Result: await stores[i % 2].SaveAsync(owner, cart, etag));
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(writers);

        var winners = results.Where(r => r.Result.Status == CartSaveStatus.Saved).ToList();
        winners.Should().ContainSingle("compare-and-set lets exactly one writer through");
        results.Count(r => r.Result.Status == CartSaveStatus.Conflict).Should().Be(49);
        var stored = await Store().GetAsync(owner);
        stored!.ETag.Should().Be(winners[0].Result.ETag);
        ShouldMatch(stored.Cart, winners[0].Cart);
    }

    [RedisFact]
    public async Task ConcurrentFirstSaves_ExactlyOneCreatesTheCart()
    {
        var owner = Guest();
        var stores = new[] { Store(), Store(SecondInstance.Value) };
        var carts = Enumerable.Range(0, 50).Select(_ => ACart()).ToArray();
        using var start = new ManualResetEventSlim();

        var writers = carts.Select((cart, i) => Task.Run(async () =>
        {
            start.Wait();
            return (Cart: cart, Result: await stores[i % 2].SaveAsync(owner, cart, null));
        })).ToArray();
        start.Set();
        var results = await Task.WhenAll(writers);

        var winner = results.Should().ContainSingle(r => r.Result.Status == CartSaveStatus.Saved).Subject;
        ShouldMatch((await Store().GetAsync(owner))!.Cart, winner.Cart);
    }

    [RedisFact]
    public async Task TheETagIsRandom_ACartThatChangesBack_StillConflictsForAnOldReader()
    {
        var store = Store();
        var owner = Guest();
        var cart = ACart();
        var (_, e1) = await store.SaveAsync(owner, cart, null);
        var (_, e2) = await store.SaveAsync(owner, ACart(), e1);
        var (_, e3) = await store.SaveAsync(owner, cart, e2);   // the same content as at e1

        e3.Should().NotBe(e1);
        (await store.SaveAsync(owner, ACart(), e1)).Status.Should().Be(CartSaveStatus.Conflict);
    }

    [RedisTheory]
    [InlineData("")]
    [InlineData("x")]
    [InlineData("0123456789ABCDEF0123456789ABCDEF")]
    [InlineData("0123456789abcdef0123456789abcde")]
    [InlineData("0123456789abcdef0123456789abcdef0")]
    public async Task Save_WithAMalformedExpectedETag_Conflicts_AndWritesNothing(string etag)
    {
        var store = Store();
        var owner = Guest();
        (await store.SaveAsync(owner, ACart(), etag)).Status.Should().Be(CartSaveStatus.Conflict, "only null means 'must not exist'");
        (await Db.KeyExistsAsync(owner.StoreKey)).Should().BeFalse();

        (await store.SaveAsync(owner, ACart(), new string('a', 1_000_000))).Status.Should().Be(CartSaveStatus.Conflict);
        (await Db.KeyExistsAsync(owner.StoreKey)).Should().BeFalse();
    }

    [RedisFact]
    public async Task Ttl_GuestIsSevenDays_UserThirtyDays()
    {
        var store = Store();
        var guest = Guest();
        var user = User();
        await store.SaveAsync(guest, ACart(), null);
        await store.SaveAsync(user, ACart(), null);

        (await Db.KeyTimeToLiveAsync(guest.StoreKey)).Should().BeCloseTo(TimeSpan.FromDays(7), TimeSpan.FromMinutes(1));
        (await Db.KeyTimeToLiveAsync(user.StoreKey)).Should().BeCloseTo(TimeSpan.FromDays(30), TimeSpan.FromMinutes(1));
        ICartStore.GuestTtl.Should().Be(TimeSpan.FromDays(7));
        ICartStore.UserTtl.Should().Be(TimeSpan.FromDays(30));
    }

    [RedisFact]
    public async Task Ttl_SlidesOnEverySave_AndOnEveryRead()
    {
        var store = Store();
        var owner = User();
        var (_, etag) = await store.SaveAsync(owner, ACart(), null);

        await Db.KeyExpireAsync(owner.StoreKey, TimeSpan.FromSeconds(60));
        (await store.GetAsync(owner)).Should().NotBeNull();
        (await Db.KeyTimeToLiveAsync(owner.StoreKey)).Should().BeCloseTo(TimeSpan.FromDays(30), TimeSpan.FromMinutes(1), "a read slides the TTL");

        await Db.KeyExpireAsync(owner.StoreKey, TimeSpan.FromSeconds(60));
        (await store.SaveAsync(owner, ACart(), etag)).Status.Should().Be(CartSaveStatus.Saved);
        (await Db.KeyTimeToLiveAsync(owner.StoreKey)).Should().BeCloseTo(TimeSpan.FromDays(30), TimeSpan.FromMinutes(1), "a save slides the TTL");
    }

    [RedisFact]
    public async Task AnExpiredCart_ReadsAsAbsent_AndCanBeCreatedAgain()
    {
        var store = Store();
        var owner = Guest();
        await store.SaveAsync(owner, ACart(), null);
        await Db.KeyExpireAsync(owner.StoreKey, TimeSpan.FromMilliseconds(1));
        await Task.Delay(50);

        (await store.GetAsync(owner)).Should().BeNull();
        (await store.SaveAsync(owner, ACart(), null)).Status.Should().Be(CartSaveStatus.Saved);
    }

    private static readonly string V = Guid.NewGuid().ToString();
    private static string Line(string variant = "", string quantity = "2", string price = "100") =>
        $"{{\"variantId\":\"{(variant.Length == 0 ? Guid.NewGuid().ToString() : variant)}\",\"quantity\":{quantity},\"seenUnitNetMinor\":{price}}}";
    private static string Doc(string items, string schema = "1", string version = "1") => $"{{\"schema\":{schema},\"version\":{version},\"items\":[{items}]}}";

    public static TheoryData<string, string> Unreadable => new()
    {
        { "not JSON", "{\"schema\":1," },
        { "empty", "" },
        { "JSON null", "null" },
        { "array", "[]" },
        { "missing fields", "{}" },
        { "missing items", "{\"schema\":1,\"version\":1}" },
        { "unknown field", "{\"schema\":1,\"version\":1,\"items\":[],\"total\":5}" },
        { "unknown line field", Doc(Line()[..^1] + ",\"unitPrice\":1}") },
        { "duplicate field", "{\"schema\":1,\"version\":1,\"version\":2,\"items\":[]}" },
        { "wrong case", "{\"Schema\":1,\"Version\":1,\"Items\":[]}" },
        { "items null", "{\"schema\":1,\"version\":1,\"items\":null}" },
        { "null line", Doc("null") },
        { "older schema", Doc(Line(), schema: "0") },
        { "negative version", Doc(Line(), version: "-1") },
        { "quantity as text", Doc(Line(quantity: "\"2\"")) },
        { "fractional quantity", Doc(Line(quantity: "2.5")) },
        { "quantity over the limit", Doc(Line(quantity: "10001")) },
        { "zero quantity", Doc(Line(quantity: "0")) },
        { "negative quantity", Doc(Line(quantity: "-3")) },
        { "negative price", Doc(Line(price: "-1")) },
        { "empty variant", Doc(Line(Guid.Empty.ToString())) },
        { "not a guid", Doc(Line("not-a-guid")) },
        { "duplicate variant", Doc(Line(V) + "," + Line(V)) },
        { "101 lines", Doc(string.Join(",", Enumerable.Range(0, 101).Select(_ => Line()))) },
        { "nested arrays", "{\"schema\":1,\"version\":1,\"items\":[[[[[[[[[[]]]]]]]]]]}" },
    };

    [RedisFact]
    public async Task AHandWrittenValidPayload_IsRead_SoTheUnreadableCasesFailForTheirOwnReason()
    {
        var owner = Guest();
        string variant = Guid.NewGuid().ToString();
        await Db.HashSetAsync(owner.StoreKey, [new("e", new string('a', 32)), new("v", Doc(Line(variant, "10000", "0") + "," + Line(), version: "7"))]);

        var stored = await Store().GetAsync(owner);
        stored!.Cart.Version.Should().Be(7);
        stored.Cart.Items.Should().HaveCount(2);
        stored.Cart.Items[0].Should().Be(new CartItem(Guid.Parse(variant), 10_000, 0));
        stored.ETag.Should().Be(new string('a', 32));
    }

    [RedisTheory]
    [MemberData(nameof(Unreadable))]
    public async Task AnUnreadableOrOverLimitCart_ReadsAsAbsent_IsDiscarded_AndANewCartCanBeSaved(string why, string payload)
    {
        var store = Store();
        var owner = User();
        await Db.HashSetAsync(owner.StoreKey, [new("e", new string('b', 32)), new("v", payload)]);

        (await store.GetAsync(owner)).Should().BeNull(why);
        (await Db.KeyExistsAsync(owner.StoreKey)).Should().BeFalse("the unreadable cart is removed so the buyer is not stuck");
        (await store.SaveAsync(owner, ACart(), null)).Status.Should().Be(CartSaveStatus.Saved);
    }

    [RedisFact]
    public async Task InvalidUtf8_AnOversizedPayload_AWrongKeyType_AMissingOrMalformedETag_AllReadAsAbsent()
    {
        var store = Store();
        var cases = new List<(string Why, Func<string, Task> Plant)>
        {
            ("invalid UTF-8", k => Db.HashSetAsync(k, [new("e", new string('c', 32)), new("v", new byte[] { 0x7B, 0xFF, 0xFE, 0x7D })])),
            ("over the size cap", k => Db.HashSetAsync(k, [new("e", new string('c', 32)), new("v", Doc(Line()) + new string(' ', RedisCartStore.MaxPayloadBytes))])),
            ("a string, not a hash", k => Db.StringSetAsync(k, Doc(Line()))),
            ("a list", k => Db.ListRightPushAsync(k, "x")),
            ("no etag field", k => Db.HashSetAsync(k, "v", Doc(Line()))),
            ("a malformed etag", k => Db.HashSetAsync(k, [new("e", "ABC"), new("v", Doc(Line()))])),
            ("no value field", k => Db.HashSetAsync(k, "e", new string('c', 32))),
        };

        foreach (var (why, plant) in cases)
        {
            var owner = Guest();
            await plant(owner.StoreKey);
            (await store.GetAsync(owner)).Should().BeNull(why);
            (await Db.KeyExistsAsync(owner.StoreKey)).Should().BeFalse(why);
            (await store.SaveAsync(owner, ACart(), null)).Status.Should().Be(CartSaveStatus.Saved, why);
        }
    }

    [RedisFact]
    public async Task ACartWrittenByANewerBuild_Is503_AndIsKept()
    {
        var store = Store();
        foreach (string payload in new[] { Doc(Line(), schema: "2"), "{\"schema\":2,\"version\":1,\"items\":[],\"currency\":\"BDT\"}" })
        {
            var owner = Guest();
            await Db.HashSetAsync(owner.StoreKey, [new("e", new string('d', 32)), new("v", payload)]);

            Func<Task> read = () => store.GetAsync(owner);
            (await read.Should().ThrowAsync<ServiceUnavailableException>()).Which.Code.Should().Be("CART_UNAVAILABLE");
            (await Db.HashGetAsync(owner.StoreKey, "v")).ToString().Should().Be(payload, "a rolling deploy must never destroy carts");
        }
    }

    public static TheoryData<string, DomainCart> OverLimit
    {
        get
        {
            var v = Guid.NewGuid();
            return new()
            {
                { "101 lines", new DomainCart([.. Enumerable.Range(0, 101).Select(_ => new CartItem(Guid.NewGuid(), 1, 1))]) },
                { "quantity over the limit", CartWith(v, quantity: 10_001) },
                { "zero quantity", CartWith(v, quantity: 0) },
                { "negative price", CartWith(v, price: -5) },
                { "duplicate variant", new DomainCart([new CartItem(v, 1, 1), new CartItem(v, 1, 1)]) },
            };
        }
    }

    [RedisTheory]
    [MemberData(nameof(OverLimit))]
    public async Task Save_OverTheLimits_IsRefused_AndWritesNothing(string why, DomainCart cart)
    {
        var owner = Guest();
        Func<Task> save = () => Store().SaveAsync(owner, cart, null);
        await save.Should().ThrowAsync<ValidationException>(why);
        (await Db.KeyExistsAsync(owner.StoreKey)).Should().BeFalse();
    }

    [RedisFact]
    public async Task AUserAndAGuest_WithTheSameId_NeverShareACart()
    {
        var store = Store();
        var id = Guid.NewGuid();
        var user = Track(CartOwner.ForUser(id));
        var guest = Track(CartOwner.FromValidatedGuestToken(new GuestCartId(id)));
        DomainCart userCart = ACart(), guestCart = ACart();

        (await store.SaveAsync(user, userCart, null)).Status.Should().Be(CartSaveStatus.Saved);
        (await store.SaveAsync(guest, guestCart, null)).Status.Should().Be(CartSaveStatus.Saved, "the guest key is a different key");
        ShouldMatch((await store.GetAsync(user))!.Cart, userCart);
        ShouldMatch((await store.GetAsync(guest))!.Cart, guestCart);

        await store.DeleteAsync(guest);
        (await store.GetAsync(guest)).Should().BeNull();
        ShouldMatch((await store.GetAsync(user))!.Cart, userCart);
    }

    [RedisFact]
    public async Task Delete_IsIdempotent_AndFreesTheCart()
    {
        var store = Store();
        var owner = User();
        await store.DeleteAsync(owner);   // nothing there yet
        var (_, etag) = await store.SaveAsync(owner, ACart(), null);

        await store.DeleteAsync(owner);
        await store.DeleteAsync(owner);
        (await store.GetAsync(owner)).Should().BeNull();
        (await store.SaveAsync(owner, ACart(), etag)).Status.Should().Be(CartSaveStatus.Conflict, "the old ETag died with the cart");
        (await store.SaveAsync(owner, ACart(), null)).Status.Should().Be(CartSaveStatus.Saved);
    }

    [Fact]
    public async Task WithoutRedis_EveryOperationIs503_NeverAnEmptyCart()
    {
        await using var down = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=200,syncTimeout=500,asyncTimeout=500");
        var store = Store(down);
        var owner = CartOwner.ForUser(Guid.NewGuid());

        Func<Task> get = () => store.GetAsync(owner);
        Func<Task> save = () => store.SaveAsync(owner, ACart(), null);
        Func<Task> delete = () => store.DeleteAsync(owner);
        (await get.Should().ThrowAsync<ServiceUnavailableException>()).Which.Code.Should().Be("CART_UNAVAILABLE");
        (await save.Should().ThrowAsync<ServiceUnavailableException>()).Which.Code.Should().Be("CART_UNAVAILABLE");
        (await delete.Should().ThrowAsync<ServiceUnavailableException>()).Which.Code.Should().Be("CART_UNAVAILABLE");
    }

    [Fact]
    public async Task AnOwnerNotMadeByTheFactories_IsRefused()
    {
        await using var down = await ConnectionMultiplexer.ConnectAsync("127.0.0.1:1,abortConnect=false,connectTimeout=200");
        Func<Task> get = () => Store(down).GetAsync(default);
        await get.Should().ThrowAsync<ArgumentException>();
    }
}

/// <summary>The API host refuses to start outside Development/Testing without a real guest-cart key (E1.2).</summary>
public class GuestCartKeyStartupTests
{
    [PostgresTheory]
    [InlineData("missing", "GuestCart:TokenKey is not set")]
    [InlineData("development", "published development key")]
    [InlineData("short", "at least 32 random bytes")]
    public async Task Production_RefusesToStart_WithoutARealGuestCartKey(string key, string message)
    {
        await using var factory = new DbBackedFactory("Production", new Dictionary<string, string>
        {
            ["Jwt:Secret"] = "prod-like-secret-" + Guid.NewGuid().ToString("N"),
            ["Storage:AccessKey"] = "test-access-key",
            ["Storage:SecretKey"] = "test-secret-key",
            // Field encryption is process-wide: keep the previous test key so parallel tests still read their data.
            ["Encryption:FieldKey"] = TestEncryption.KeyBase64,
            ["Encryption:PreviousFieldKeys"] = TestEncryption.PreviousKeyBase64,
            ["Documents:LocalPath"] = TestEncryption.DocumentsPath,
            ["Antivirus:Host"] = "127.0.0.1",
            ["Email:Host"] = "localhost",
            ["Email:FromAddress"] = "no-reply@mechware.test",
            ["Storefront:BaseUrl"] = "https://shop.mechware.test",
            ["Search:ApiKey"] = "test-search-key",
            ["GuestCart:TokenKey"] = key switch
            {
                "development" => Convert.ToBase64String(GuestCartTokenOptions.DevelopmentKey),
                "short" => Convert.ToBase64String(Encoding.UTF8.GetBytes("sixteen-bytes!!!")),
                _ => "",
            },
        });
        FluentActions.Invoking(() => factory.CreateClient()).Should().Throw<Exception>().Where(e => e.ToString().Contains(message));
    }

    [PostgresFact]
    public async Task Production_Starts_WithARealGuestCartKey_TheControl()
    {
        await using var factory = new DbBackedFactory("Production", new Dictionary<string, string>
        {
            ["Jwt:Secret"] = "prod-like-secret-" + Guid.NewGuid().ToString("N"),
            ["Storage:AccessKey"] = "test-access-key",
            ["Storage:SecretKey"] = "test-secret-key",
            // Field encryption is process-wide: keep the previous test key so parallel tests still read their data.
            ["Encryption:FieldKey"] = TestEncryption.KeyBase64,
            ["Encryption:PreviousFieldKeys"] = TestEncryption.PreviousKeyBase64,
            ["Documents:LocalPath"] = TestEncryption.DocumentsPath,
            ["Antivirus:Host"] = "127.0.0.1",
            ["Email:Host"] = "localhost",
            ["Email:FromAddress"] = "no-reply@mechware.test",
            ["Storefront:BaseUrl"] = "https://shop.mechware.test",
            ["Search:ApiKey"] = "test-search-key",
        });
        using var client = factory.CreateClient();
        var tokens = factory.Services.GetService(typeof(IGuestCartTokenService)) as IGuestCartTokenService;
        tokens.Should().NotBeNull();
        factory.Services.GetService(typeof(ICartStore)).Should().BeOfType<RedisCartStore>();
        tokens!.Validate(tokens.Issue(GuestCartId.New())).Should().NotBeNull();
    }
}
