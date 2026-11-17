using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Mechware.Application.Credit;

namespace Mechware.Infrastructure.Workers;

/// <summary>
/// Nightly at 02:30 Dhaka time, checks every company's credit books (ADR-0010 §11, threat model CS28) and records each mismatch once a
/// day: an error log (company id and check name only, no personal data), an audit row and an outbox event
/// <c>credit.reconciliation_mismatch</c>, and the metric <c>credit_reconciliation_mismatches</c>. It never repairs anything. Read-only
/// snapshots without row locks, so it is safe next to verifications, bounces and cancellations; several Worker replicas may run it.
/// Hosted in <c>Mechware.Worker</c> only, like <see cref="CreditHoldWorker"/>.
/// </summary>
public sealed class CreditReconciliationWorker(IServiceScopeFactory scopes, TimeProvider clock, ILogger<CreditReconciliationWorker> logger) : BackgroundService
{
    /// <summary>02:30 in Dhaka (fixed +06:00, no daylight saving) = 20:30 UTC.</summary>
    public static readonly TimeSpan RunAtDhaka = new(2, 30, 0);

    /// <summary>The next 02:30 Dhaka strictly after <paramref name="nowUtc"/>.</summary>
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
                var report = await scope.ServiceProvider.GetRequiredService<ICreditReconciliationService>().RunAsync(clock.GetUtcNow().UtcDateTime, stoppingToken);
                foreach (var f in report.Findings)
                    logger.LogError("Credit reconciliation mismatch: company {CompanyId}, check {Check}, subject {SubjectId}", f.CompanyId, f.Check, f.SubjectId);
                logger.LogInformation("Credit reconciliation {RunOn}: {Companies} companies, {Findings} mismatches ({New} new), {Waiting} payments recorded over 7 days, {Cheques} post-dated cheques due",
                    report.RunOn, report.CompaniesChecked, report.Findings.Count, report.NewlyRecorded, report.PaymentsWaitingOver7Days, report.PostDatedChequesDue);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Credit reconciliation failed");
            }
        }
    }
}
