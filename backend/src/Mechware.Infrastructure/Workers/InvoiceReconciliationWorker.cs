using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mechware.Application.Invoicing;

namespace Mechware.Infrastructure.Workers;

/// <summary>
/// Nightly at 03:00 Dhaka time (after the credit pass at 02:30), checks every invoice (E6.3d, ADR-0012 §17, threat model IT13, IT16). Each
/// finding is an ALERT error log (check and ids only, no personal data), an audit row and an outbox event
/// <c>invoice.reconciliation_finding</c>, and the metric <c>invoice_reconciliation_findings</c>. It never repairs anything. One read-only
/// snapshot without row locks; the advisory lock lets only one of several Worker replicas run a pass. Hosted in <c>Mechware.Worker</c> only.
/// </summary>
public sealed class InvoiceReconciliationWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<InvoiceReconciliationWorker> logger) : BackgroundService
{
    /// <summary>03:00 in Dhaka (fixed +06:00, no daylight saving) = 21:00 UTC.</summary>
    public static readonly TimeSpan RunAtDhaka = new(3, 0, 0);

    /// <summary>The next 03:00 Dhaka strictly after <paramref name="nowUtc"/>.</summary>
    public static DateTime NextRunUtc(DateTime nowUtc)
    {
        var dhaka = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc).AddHours(6);
        var next = dhaka.Date + RunAtDhaka;
        if (next <= dhaka) next = next.AddDays(1);
        return DateTime.SpecifyKind(next.AddHours(-6), DateTimeKind.Utc);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = clock.GetUtcNow().UtcDateTime;
            try { await Task.Delay(NextRunUtc(now) - now, clock, stoppingToken); }
            catch (OperationCanceledException) { return; }
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var report = await scope.ServiceProvider.GetRequiredService<IInvoiceReconciliationService>().RunAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
                if (!report.Ran) logger.LogInformation("Invoice reconciliation {RunOn}: another pass is running; skipped.", report.RunOn);
                else
                    logger.LogInformation("Invoice reconciliation {RunOn}: {Invoices} invoices, {Findings} findings ({New} new)",
                        report.RunOn, report.InvoicesChecked, report.Findings.Count, report.NewlyRecorded);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "ALERT invoice reconciliation failed");
            }
        }
    }
}
