using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mechware.Application.Credit;

namespace Mechware.Infrastructure.Workers;

/// <summary>
/// Every 15 minutes, notes companies whose overdue credit hold started or ended (ADR-0010 §9, B3.7) and emits
/// <c>credit.hold_started</c> / <c>credit.hold_ended</c> through the outbox (reminders: F3). It enforces nothing: placement and
/// approval check the ledger themselves under the company lock, so a late or missed pass never lets an order through. Idempotent, so
/// several Worker replicas may run it. Hosted in <c>Mechware.Worker</c> only.
/// </summary>
public sealed class CreditHoldWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CreditHoldWorker> logger) : BackgroundService
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
                int changed;
                do
                {
                    await using var scope = scopes.CreateAsyncScope();
                    var holds = scope.ServiceProvider.GetRequiredService<ICreditHoldService>();
                    changed = await holds.SweepAsync(clock.GetUtcNow().UtcDateTime, Batch, stoppingToken);
                    if (changed > 0) logger.LogInformation("Credit hold sweep: {Count} companies started or ended a hold", changed);
                } while (changed == Batch && !stoppingToken.IsCancellationRequested);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Credit hold sweep failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
