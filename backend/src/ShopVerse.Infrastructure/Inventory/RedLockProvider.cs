using RedLockNet;
using RedLockNet.SERedis;
using RedLockNet.SERedis.Configuration;
using ShopVerse.Application.Common;
using StackExchange.Redis;

namespace ShopVerse.Infrastructure.Inventory;

/// <summary>Redis RedLock (RedLock.net) distributed locks for flash-sale contention (ADR 0004 tier 1).</summary>
public sealed class RedLockProvider : IDistributedLockProvider, IDisposable
{
    private readonly RedLockFactory _factory;

    public RedLockProvider(IConnectionMultiplexer redis)
    {
        _factory = RedLockFactory.Create([new RedLockMultiplexer(redis)]);
    }

    public async Task<IAsyncDisposable?> TryAcquireAsync(
        string resource, TimeSpan expiry, TimeSpan wait, CancellationToken ct = default)
    {
        var redLock = await _factory.CreateLockAsync(resource, expiry, wait, TimeSpan.FromMilliseconds(25), ct);
        return redLock.IsAcquired ? new Handle(redLock) : null;
    }

    public void Dispose() => _factory.Dispose();

    private sealed class Handle(IRedLock inner) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => inner.DisposeAsync();
    }
}

/// <summary>No-op lock used when Redis is unavailable: PostgreSQL row locks still guarantee correctness.</summary>
public sealed class NoopLockProvider : IDistributedLockProvider
{
    private sealed class Nothing : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    public Task<IAsyncDisposable?> TryAcquireAsync(string resource, TimeSpan expiry, TimeSpan wait, CancellationToken ct = default) =>
        Task.FromResult<IAsyncDisposable?>(new Nothing());
}
