using Mechware.Infrastructure.Notifications;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mechware.Infrastructure.Workers;

/// <summary>Once a day deletes in-app notices read more than 180 days ago (F3c). Not audit data. Hosted in <c>Mechware.Worker</c> only.</summary>
public sealed class InAppNotificationRetentionWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<InAppNotificationRetentionWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(24);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var deleted = await scope.ServiceProvider.GetRequiredService<InAppNotificationService>().PurgeAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
                if (deleted > 0) logger.LogInformation("In-app notifications: {Count} old read rows deleted", deleted);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "In-app notification retention failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
