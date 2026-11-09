using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mechware.Application.Payments;

namespace Mechware.Infrastructure.Workers;

/// <summary>
/// Once a minute, asks the payment providers about attempts whose webhook never came (threat model v2, P4), so a lost webhook
/// does not leave a paid order to be cancelled by the unpaid-order expiry. Runs where the payment providers are registered.
/// </summary>
public sealed class PaymentReconciliationWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<PaymentReconciliationWorker> logger)
    : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    private const int Batch = 100;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        while (await WaitAsync(timer, stoppingToken))
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var payments = scope.ServiceProvider.GetRequiredService<IPaymentService>();
                var confirmed = await payments.ReconcileAsync(clock.GetUtcNow().UtcDateTime, Batch, stoppingToken);
                if (confirmed > 0) logger.LogWarning("Reconciliation confirmed {Count} paid orders whose webhook was missing", confirmed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Payment reconciliation pass failed");
            }
        }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
