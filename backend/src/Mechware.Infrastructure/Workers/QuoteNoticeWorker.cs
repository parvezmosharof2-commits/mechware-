using Mechware.Infrastructure.Quotes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mechware.Infrastructure.Workers;

/// <summary>
/// Every 15 minutes queues the quote expiry notices (48 h before and after <c>valid_until</c>, <see cref="QuoteNoticeService"/>). It never
/// changes a quote status. Deterministic event ids make several passes or Worker replicas harmless. Hosted in <c>Mechware.Worker</c> only.
/// </summary>
public sealed class QuoteNoticeWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<QuoteNoticeWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var written = await scope.ServiceProvider.GetRequiredService<QuoteNoticeService>().SweepAsync(clock.GetUtcNow().UtcDateTime, Batch, stoppingToken);
                if (written > 0) logger.LogInformation("Quote notices: {Count} queued", written);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Quote notice sweep failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
