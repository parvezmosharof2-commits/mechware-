using Mechware.Infrastructure.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mechware.Infrastructure.Workers;

/// <summary>
/// E7.3: every <see cref="StockReconciliationOptions.Interval"/> (default 10 minutes) finishes the stock step of orders a crash left
/// half done (<see cref="StockReconciliationService"/>). The default interval plus grace period stays inside the 30-minute hold of an
/// unpaid order, so a confirmed order's uncommitted hold is normally committed before the expiry sweep could let it lapse. An advisory
/// lock lets several Worker replicas run it: one sweeps, the others skip. Hosted in <c>Mechware.Worker</c> only.
/// </summary>
public sealed class StockReconciliationWorker(IServiceScopeFactory scopes, StockReconciliationOptions options, TimeProvider clock,
    ILogger<StockReconciliationWorker> logger) : BackgroundService
{
    /// <summary>One pass, in its own scope (also used by tests).</summary>
    public async Task<StockReconciliationPass> RunOnceAsync(CancellationToken ct = default)
    {
        await using var scope = scopes.CreateAsyncScope();
        var pass = await scope.ServiceProvider.GetRequiredService<StockReconciliationService>().RunOnceAsync(clock.GetUtcNow().UtcDateTime, ct);
        if (pass.Fixed > 0 || pass.Failed > 0)
            logger.LogWarning("Stock reconciliation: {Fixed} orders fixed, {Failed} failed (retried next pass)", pass.Fixed, pass.Failed);
        return pass;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(options.Interval);
        do
        {
            try { await RunOnceAsync(stoppingToken); }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Stock reconciliation pass failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
