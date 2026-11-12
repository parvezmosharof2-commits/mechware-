using StackExchange.Redis;

namespace Mechware.Infrastructure.Search;

/// <summary>
/// A short Redis lease that makes one indexer instance the only consumer of the search queue. Each tick the holder renews it
/// (value-checked, so it never extends someone else's lease); if the holder dies the lease lapses after <see cref="Ttl"/>.
/// </summary>
public sealed class SearchIndexLease(IConnectionMultiplexer redis, string instanceId)
{
    public const string LockKey = "search:indexer:lock";
    public static readonly TimeSpan Ttl = TimeSpan.FromSeconds(30);

    internal string Key { get; init; } = LockKey;

    /// <summary>True when this instance holds the lease after the call (newly taken or renewed).</summary>
    public async Task<bool> TryAcquireOrRenewAsync()
    {
        var db = redis.GetDatabase();
        if (await db.LockExtendAsync(Key, instanceId, Ttl)) return true;
        return await db.LockTakeAsync(Key, instanceId, Ttl);
    }

    public async Task ReleaseAsync() => await redis.GetDatabase().LockReleaseAsync(Key, instanceId);
}
