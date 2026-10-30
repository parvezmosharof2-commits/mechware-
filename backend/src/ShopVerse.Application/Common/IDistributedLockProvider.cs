namespace ShopVerse.Application.Common;

/// <summary>
/// Cross-node mutual exclusion (Redis RedLock in production). Used as a contention guard in front of
/// PostgreSQL row locks; correctness never depends on it (see ADR 0004).
/// </summary>
public interface IDistributedLockProvider
{
    /// <summary>Acquires the lock or returns null when it could not be obtained within <paramref name="wait"/>.</summary>
    Task<IAsyncDisposable?> TryAcquireAsync(string resource, TimeSpan expiry, TimeSpan wait, CancellationToken ct = default);
}
