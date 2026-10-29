using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopVerse.Application.Inventory;
using ShopVerse.Infrastructure.Persistence.Outbox;

namespace ShopVerse.Infrastructure.Workers;

/// <summary>Releases stock reservations older than their 15 minute TTL, once a minute (ADR 0004).</summary>
public sealed class ReservationExpiryWorker : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private const int Batch = 500;

    private readonly IServiceScopeFactory _scopes;
    private readonly TimeProvider _clock;
    private readonly ILogger<ReservationExpiryWorker> _logger;

    public ReservationExpiryWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ReservationExpiryWorker> logger)
    {
        _scopes = scopes;
        _clock = clock;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                int released;
                do
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var inventory = scope.ServiceProvider.GetRequiredService<IInventoryService>();
                    released = await inventory.ReleaseExpiredAsync(_clock.GetUtcNow().UtcDateTime, Batch, stoppingToken);
                    if (released > 0) _logger.LogInformation("Released {Count} expired stock reservations", released);
                } while (released == Batch && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Reservation expiry sweep failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}

/// <summary>Relays outbox messages to the search sync queue and cache invalidation.</summary>
public sealed class OutboxRelayWorker : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private readonly IServiceScopeFactory _scopes;
    private readonly ILogger<OutboxRelayWorker> _logger;

    public OutboxRelayWorker(IServiceScopeFactory scopes, ILogger<OutboxRelayWorker> logger)
    {
        _scopes = scopes;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                int delivered;
                do
                {
                    await using var scope = _scopes.CreateAsyncScope();
                    var relay = scope.ServiceProvider.GetRequiredService<OutboxRelay>();
                    delivered = await relay.ProcessBatchAsync(100, stoppingToken);
                } while (delivered > 0 && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogError(ex, "Outbox relay iteration failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
