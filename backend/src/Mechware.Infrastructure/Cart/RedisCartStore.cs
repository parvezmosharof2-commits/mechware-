using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using Mechware.Application.Cart;
using Mechware.Domain.Cart;
using Mechware.Domain.Common;
using Mechware.Domain.Common.Exceptions;
using StackExchange.Redis;
using DomainCart = Mechware.Domain.Cart.Cart;

namespace Mechware.Infrastructure.Cart;

/// <summary>
/// <see cref="ICartStore"/> on Redis. One hash per cart at <see cref="CartOwner.StoreKey"/>: field <c>e</c> = ETag (128
/// random bits, hex), field <c>v</c> = the cart as versioned JSON. Every operation is one Lua script, so it is atomic. All
/// are single-key except the login merge (<see cref="SaveMergedAsync"/>: user + guest key), so cart keys must live on one
/// Redis node (a single instance or a dedicated one, no Redis Cluster).
/// <list type="bullet">
/// <item>Save is compare-and-set: it writes only when the stored ETag equals the expected one (<c>null</c> = the key must
/// not exist), and a conflict writes nothing. A random ETag (not a content hash) means a cart that changed and changed back
/// still conflicts.</item>
/// <item>TTL slides on every save and on every read that finds the cart, so a cart the buyer keeps opening does not expire
/// under them; the guest token is re-issued on each response for the same reason (E1.3).</item>
/// <item>Stored data is not trusted: on load the size is capped before the value is fetched, the JSON is strict (unknown or
/// duplicate fields refused) and the <see cref="OrderLimits"/> are checked again. An unreadable cart is logged, removed
/// (only if still unchanged) and read as absent, so the buyer can start a new one. A cart written by a newer build
/// (higher schema) is left alone and the request gets 503, so a rolling deploy never destroys carts.</item>
/// <item>Redis unavailable: <see cref="ServiceUnavailableException"/> (503), never an empty cart that the buyer would then
/// overwrite.</item>
/// </list>
/// </summary>
public sealed class RedisCartStore(IConnectionMultiplexer redis, ILogger<RedisCartStore> logger) : ICartStore
{
    /// <summary>
    /// Changing the stored format takes two releases: first a build that reads schema N+1 but still writes N, then the
    /// build that writes N+1. Never roll back past the reader, or older builds answer 503 for the newer carts until they expire.
    /// </summary>
    public const int SchemaVersion = 1;

    /// <summary>A full cart (100 lines) is about 10 KB.</summary>
    public const int MaxPayloadBytes = 32 * 1024;

    // KEYS[1] cart. ARGV: 1 max payload bytes, 2 ttl seconds. Nil = absent, else {status, etag, payload}.
    private const string ReadScript = """
        local t = redis.call('TYPE', KEYS[1])['ok']
        if t == 'none' then return false end
        if t ~= 'hash' then return {'type', '', ''} end
        local e = redis.call('HGET', KEYS[1], 'e')
        if not e then return {'etag', '', ''} end
        if redis.call('HSTRLEN', KEYS[1], 'v') > tonumber(ARGV[1]) then return {'size', e, ''} end
        redis.call('EXPIRE', KEYS[1], ARGV[2])
        return {'ok', e, redis.call('HGET', KEYS[1], 'v') or ''}
        """;

    // KEYS[1] cart. ARGV: 1 expected etag ('' = must not exist), 2 new etag, 3 payload, 4 ttl seconds. 1 saved, 0 conflict.
    private const string SaveScript = """
        local t = redis.call('TYPE', KEYS[1])['ok']
        if ARGV[1] == '' then
          if t ~= 'none' then return 0 end
        elseif t ~= 'hash' or redis.call('HGET', KEYS[1], 'e') ~= ARGV[1] then
          return 0
        end
        redis.call('HSET', KEYS[1], 'e', ARGV[2], 'v', ARGV[3])
        redis.call('EXPIRE', KEYS[1], ARGV[4])
        return 1
        """;

    // KEYS[1] cart. ARGV[1] etag seen when it was found unreadable. Deletes it only if nobody has saved since.
    private const string DiscardScript = """
        local t = redis.call('TYPE', KEYS[1])['ok']
        if t == 'none' then return 0 end
        if t == 'hash' and (redis.call('HGET', KEYS[1], 'e') or '') ~= ARGV[1] then return 0 end
        redis.call('DEL', KEYS[1])
        return 1
        """;

    // KEYS[1] user cart, KEYS[2] guest cart. ARGV: 1 expected user etag ('' = must not exist), 2 guest etag that was merged,
    // 3 new user etag, 4 payload, 5 user ttl seconds. 1 = user cart saved and guest cart deleted, 0 = conflict, nothing written.
    // The only two-key script: the save and the delete must be one step, so both keys must be on one node (no Redis Cluster;
    // on a cluster Redis refuses it with CROSSSLOT, which is a 503 and the login goes on without merging).
    private const string MergeScript = """
        local t = redis.call('TYPE', KEYS[1])['ok']
        if ARGV[1] == '' then
          if t ~= 'none' then return 0 end
        elseif t ~= 'hash' or redis.call('HGET', KEYS[1], 'e') ~= ARGV[1] then
          return 0
        end
        if redis.call('TYPE', KEYS[2])['ok'] ~= 'hash' or redis.call('HGET', KEYS[2], 'e') ~= ARGV[2] then return 0 end
        redis.call('HSET', KEYS[1], 'e', ARGV[3], 'v', ARGV[4])
        redis.call('EXPIRE', KEYS[1], ARGV[5])
        redis.call('DEL', KEYS[2])
        return 1
        """;

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        AllowDuplicateProperties = false,
        RespectNullableAnnotations = true,
        RespectRequiredConstructorParameters = true,
        MaxDepth = 8,
    };

    private sealed record CartDocument(int Schema, int Version, CartLine[] Items);

    private sealed record CartLine(Guid VariantId, int Quantity, long SeenUnitNetMinor);

    public async Task<StoredCart?> GetAsync(CartOwner owner, CancellationToken ct = default)
    {
        RedisResult result = await RunAsync(ReadScript, [Key(owner)], [MaxPayloadBytes, TtlSeconds(owner)], ct);
        if (result.IsNull) return null;
        var parts = (RedisResult[])result!;
        string status = (string)parts[0]!, etag = (string)parts[1]!;
        byte[] payload = (byte[]?)parts[2] ?? [];

        string? problem = status != "ok" ? status : IsETag(etag) ? null : "etag";
        DomainCart? cart = null;
        if (problem is null) (cart, problem) = Parse(payload);
        if (cart is not null) return new StoredCart(cart, etag);

        if (problem == "newer-schema")
        {
            logger.LogWarning("The {Kind} cart {Owner} was written by a newer build; it is kept and this request is refused.", owner.Kind, Short(owner));
            throw Unavailable();
        }

        logger.LogError("The stored {Kind} cart {Owner} is unreadable ({Problem}, {Bytes} bytes); it is discarded and read as absent.",
            owner.Kind, Short(owner), problem, payload.Length);
        await RunAsync(DiscardScript, [Key(owner)], [etag], ct);
        return null;
    }

    public async Task<(CartSaveStatus Status, string? ETag)> SaveAsync(CartOwner owner, DomainCart cart, string? expectedETag, CancellationToken ct = default)
    {
        byte[] payload = Serialize(cart);

        // A malformed ETag can never equal a stored one; it is answered without sending it to Redis.
        if (expectedETag is not null && !IsETag(expectedETag)) return (CartSaveStatus.Conflict, null);

        string etag = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        int saved = (int)await RunAsync(SaveScript, [Key(owner)], [expectedETag ?? "", etag, payload, TtlSeconds(owner)], ct);
        return saved == 1 ? (CartSaveStatus.Saved, etag) : (CartSaveStatus.Conflict, null);
    }

    public async Task<(CartSaveStatus Status, string? ETag)> SaveMergedAsync(
        CartOwner user, DomainCart merged, string? expectedUserETag, CartOwner guest, string guestETag, CancellationToken ct = default)
    {
        if (user.Kind != CartOwnerKind.User) throw new ArgumentException("The merged cart belongs to a user.", nameof(user));
        if (guest.Kind != CartOwnerKind.Guest) throw new ArgumentException("Only a guest cart is merged away.", nameof(guest));
        byte[] payload = Serialize(merged);
        if ((expectedUserETag is not null && !IsETag(expectedUserETag)) || guestETag is null || !IsETag(guestETag)) return (CartSaveStatus.Conflict, null);

        string etag = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        int saved = (int)await RunAsync(MergeScript, [Key(user), Key(guest)], [expectedUserETag ?? "", guestETag, etag, payload, TtlSeconds(user)], ct);
        return saved == 1 ? (CartSaveStatus.Saved, etag) : (CartSaveStatus.Conflict, null);
    }

    private static byte[] Serialize(DomainCart cart)
    {
        ArgumentNullException.ThrowIfNull(cart);
        string? problem = Violation(cart);
        if (problem is not null) throw new ValidationException("cart", $"The cart cannot be stored ({problem}).");
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            new CartDocument(SchemaVersion, cart.Version, [.. cart.Items.Select(i => new CartLine(i.VariantId, i.Quantity, i.SeenUnitNetMinor))]), Json);
        return payload.Length <= MaxPayloadBytes ? payload : throw new ValidationException("cart", "The cart is too large to store.");
    }

    public async Task DeleteAsync(CartOwner owner, CancellationToken ct = default)
    {
        string key = Key(owner);
        ct.ThrowIfCancellationRequested();
        try { await redis.GetDatabase().KeyDeleteAsync(key); }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { throw Unavailable(ex); }
    }

    /// <summary>Why a cart may not be stored, or null. The same rules apply on save and on load.</summary>
    internal static string? Violation(DomainCart cart)
    {
        if (cart.Items is null) return "no items";
        if (cart.Version < 0) return "negative version";
        if (cart.Items.Count > OrderLimits.MaxCartLines) return "too many lines";
        var seen = new HashSet<Guid>();
        foreach (CartItem? item in cart.Items)
        {
            if (item is null || item.VariantId == Guid.Empty) return "line without a variant";
            if (item.Quantity is < 1 or > OrderLimits.MaxQuantityPerLine) return "quantity out of range";
            if (item.SeenUnitNetMinor < 0) return "negative price";
            if (!seen.Add(item.VariantId)) return "duplicate variant";
        }
        return null;
    }

    private static (DomainCart? Cart, string? Problem) Parse(byte[] payload)
    {
        CartDocument? doc;
        try { doc = JsonSerializer.Deserialize<CartDocument>(payload, Json); }
        catch (JsonException) { doc = null; }

        if (doc is null) return (null, IsNewerSchema(payload) ? "newer-schema" : "malformed");
        if (doc.Schema != SchemaVersion) return (null, doc.Schema > SchemaVersion ? "newer-schema" : "schema");
        if (doc.Items.Any(i => i is null)) return (null, "malformed");
        var cart = new DomainCart([.. doc.Items.Select(i => new CartItem(i.VariantId, i.Quantity, i.SeenUnitNetMinor))], doc.Version);
        string? problem = Violation(cart);
        return problem is null ? (cart, null) : (null, problem);
    }

    /// <summary>A future schema may add fields this build refuses; it is recognised by its schema number alone.</summary>
    private static bool IsNewerSchema(byte[] payload)
    {
        try
        {
            using var document = JsonDocument.Parse(payload);
            return document.RootElement.ValueKind == JsonValueKind.Object
                   && document.RootElement.TryGetProperty("schema", out var schema)
                   && schema.TryGetInt32(out int number) && number > SchemaVersion;
        }
        catch (JsonException) { return false; }
    }

    private async Task<RedisResult> RunAsync(string script, RedisKey[] keys, RedisValue[] args, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try { return await redis.GetDatabase().ScriptEvaluateAsync(script, keys, args); }
        catch (Exception ex) when (ex is RedisException or TimeoutException) { throw Unavailable(ex); }   // RedisTimeoutException is a TimeoutException
    }

    private ServiceUnavailableException Unavailable(Exception? ex = null)
    {
        if (ex is not null) logger.LogError(ex, "The cart store (Redis) is unavailable; the request is refused instead of showing an empty cart.");
        return new ServiceUnavailableException("CART_UNAVAILABLE", "Your cart cannot be reached right now. Please try again shortly.");
    }

    private static string Key(CartOwner owner) =>
        (owner.Kind is CartOwnerKind.User or CartOwnerKind.Guest) && owner.Id != Guid.Empty
            ? owner.StoreKey
            : throw new ArgumentException("A cart owner is required.", nameof(owner));

    private static long TtlSeconds(CartOwner owner) =>
        (long)(owner.Kind == CartOwnerKind.User ? ICartStore.UserTtl : ICartStore.GuestTtl).TotalSeconds;

    private static bool IsETag(string etag) => etag.Length == 32 && etag.All(char.IsAsciiHexDigitLower);

    private static string Short(CartOwner owner) => owner.Id.ToString("N")[..8];
}
