using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Credit;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;

namespace Mechware.Infrastructure.Credit;

/// <summary>
/// ADR-0010 §11 (B3.10, threat model CS28). Reads: one REPEATABLE READ snapshot per batch of companies, no row lock of any kind, so it
/// never waits for (or blocks) a verification, bounce, cancel or placement, and every company's books are seen between two of their
/// commits. Writes: only audit rows and outbox events, under a transaction-scoped advisory lock (not a row lock) so parallel passes
/// record a finding once per Dhaka day (its row ids are derived from the day, company, check and subject). Never repairs anything.
/// </summary>
public sealed class CreditReconciliationService(MechwareDbContext db, CreditOptions options, int batchSize = CreditReconciliationService.BatchSize)
    : ICreditReconciliationService
{
    public const string MismatchEvent = "credit.reconciliation_mismatch";
    public const int BatchSize = 200;
    /// <summary>The hold sweep runs every 15 minutes; a hold note is compared only with a ledger that has not changed for this long.</summary>
    public static readonly TimeSpan HoldNoteGrace = TimeSpan.FromHours(1);
    private static readonly Meter Meter = new("Mechware.Credit");
    private static readonly Counter<long> Mismatches = Meter.CreateCounter<long>("credit_reconciliation_mismatches", description: "Credit reconciliation mismatches recorded");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CreditReconciliationReport> RunAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var runOn = CreditCalendar.DhakaDate(nowUtc);
        var hold = new CreditHoldExpectation(options.HoldDueBefore(nowUtc), options.HoldDueBefore(nowUtc - HoldNoteGrace), nowUtc - HoldNoteGrace,
            nowUtc.AddDays(options.HoldOverrideMaxDays) + HoldNoteGrace);
        var companies = await db.Companies.AsNoTracking().OrderBy(c => c.Id).Select(c => c.Id).ToListAsync(ct);
        var findings = new List<CreditReconciliationFinding>();
        foreach (var batch in companies.Chunk(Math.Max(1, batchSize)))
        {
            db.ChangeTracker.Clear();
            var books = await CreditReadService.SnapshotAsync(db, () => BooksAsync(batch, ct), ct);
            findings.AddRange(books.SelectMany(b => CreditReconciliation.Check(b, hold)));
        }

        // Lines whose company row is gone (only possible with the foreign keys switched off).
        findings.AddRange((await db.CreditLedgerEntries.AsNoTracking().Where(l => !db.Companies.Any(c => c.Id == l.CompanyId))
            .Select(l => l.CompanyId).Distinct().ToListAsync(ct)).Select(id => new CreditReconciliationFinding(id, CreditReconciliation.UnknownCompany, null, 0, 0)));
        var waiting = await db.CreditPayments.CountAsync(p => p.Status == CreditPaymentStatus.Recorded && p.CreatedAtUtc < nowUtc.AddDays(-7), ct);
        var chequesDue = await db.CreditPayments.CountAsync(p => p.Status == CreditPaymentStatus.Recorded && p.ChequeDate > p.ReceivedOn && p.ChequeDate <= runOn, ct);
        return new CreditReconciliationReport(runOn, companies.Count, findings, await RecordAsync(runOn, findings, nowUtc, ct), waiting, chequesDue);
    }

    private async Task<List<CreditBook>> BooksAsync(Guid[] ids, CancellationToken ct)
    {
        var companies = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id))
            .Select(c => new CreditBookCompany(c.Id, c.CreditUsedMinor, c.CreditOnAccountMinor, c.CreditLedgerSeq, c.CreditHoldSinceUtc, c.CreditHoldOverrideUntilUtc))
            .ToListAsync(ct);
        var lines = (await db.CreditLedgerEntries.AsNoTracking().Where(l => ids.Contains(l.CompanyId))
            .Select(l => new
            {
                l.CompanyId,
                Line = new CreditBookLine(l.Seq, l.Kind, l.OrderId, l.PaymentId, l.AdjustmentId, l.CreditNoteId, l.ReceivableDeltaMinor, l.OnAccountDeltaMinor,
                    l.ReceivableAfterMinor, l.OnAccountAfterMinor, l.DueOn, l.CreatedAtUtc)
            }).ToListAsync(ct)).ToLookup(x => x.CompanyId, x => x.Line);
        var all = lines.SelectMany(g => g).ToList();
        var orderIds = all.Where(l => l.OrderId is not null).Select(l => l.OrderId!.Value).Distinct().ToList();
        var paymentIds = all.Where(l => l.PaymentId is not null).Select(l => l.PaymentId!.Value).Distinct().ToList();
        var adjustmentIds = all.Where(l => l.AdjustmentId is not null).Select(l => l.AdjustmentId!.Value).Distinct().ToList();

        // A credit order counts as confirmed with a confirmation time or a Confirmed event (orders confirmed before E8.3), as the backfill.
        var orders = await db.Orders.AsNoTracking()
            .Select(o => new
            {
                Order = o,
                Confirmed = o.ConfirmedAtUtc != null || db.OrderEvents.Any(e => e.OrderId == o.Id && e.ToStatus == OrderStatus.Confirmed)
            })
            .Where(x => orderIds.Contains(x.Order.Id) || (x.Order.PaysOnCredit && x.Order.CompanyId != null && ids.Contains(x.Order.CompanyId.Value) && x.Confirmed))
            .Select(x => new CreditBookOrder(x.Order.Id, x.Order.CompanyId, x.Order.PaysOnCredit, x.Confirmed, x.Order.Status == OrderStatus.Cancelled, x.Order.GrandTotalMinor))
            .ToListAsync(ct);
        var payments = await db.CreditPayments.AsNoTracking().Where(p => ids.Contains(p.CompanyId) || paymentIds.Contains(p.Id))
            .Select(p => new CreditBookPayment(p.Id, p.CompanyId, p.Status, p.AmountMinor)).ToListAsync(ct);
        var adjustments = await db.CreditAdjustments.AsNoTracking().Where(a => ids.Contains(a.CompanyId) || adjustmentIds.Contains(a.Id))
            .Select(a => new CreditBookAdjustment(a.Id, a.CompanyId, a.Status, a.Kind, a.AmountMinor, a.OrderId, a.PaymentId, a.CreditNoteId)).ToListAsync(ct);

        return companies.Select(c =>
        {
            var mine = lines[c.Id].ToList();
            var named = mine.SelectMany(l => new[] { l.OrderId, l.PaymentId, l.AdjustmentId }).OfType<Guid>().ToHashSet();
            return new CreditBook(c, mine, orders.Where(o => o.CompanyId == c.Id || named.Contains(o.Id)).ToList(),
                payments.Where(p => p.CompanyId == c.Id || named.Contains(p.Id)).ToList(),
                adjustments.Where(a => a.CompanyId == c.Id || named.Contains(a.Id)).ToList());
        }).ToList();
    }

    /// <summary>One audit row and one outbox event per finding not yet recorded on <paramref name="runOn"/>; returns how many were new.</summary>
    private async Task<int> RecordAsync(DateOnly runOn, IReadOnlyList<CreditReconciliationFinding> findings, DateTime now, CancellationToken ct)
    {
        if (findings.Count == 0) return 0;
        db.ChangeTracker.Clear();
        var keyed = findings.Select(f => (Id: FindingId(runOn, f), Finding: f)).DistinctBy(x => x.Id).ToList();
        var ids = keyed.Select(x => x.Id).ToList();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(hashtextextended('credit-reconciliation', 0))", ct);
        var done = (await db.AuditLogs.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var fresh = keyed.Where(x => !done.Contains(x.Id)).ToList();
        foreach (var (id, f) in fresh)
        {
            var details = new { EventId = id, RunOn = runOn, f.Check, f.SubjectId, f.Expected, f.Actual };
            db.AuditLogs.Add(new AuditLog(id, null, null, MismatchEvent, "companies", f.CompanyId.ToString(), null, JsonSerializer.Serialize(details, Json), null, now));
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = id,
                EventType = MismatchEvent,
                AggregateType = nameof(Company),
                AggregateId = f.CompanyId,
                OccurredAtUtc = now,
                Payload = JsonSerializer.Serialize(new { EventId = id, f.CompanyId, RunOn = runOn, f.Check, f.SubjectId }, Json)
            });
        }
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        if (fresh.Count > 0) Mismatches.Add(fresh.Count);
        return fresh.Count;
    }

    /// <summary>A UUID (version 8) of SHA-256 over the day, company, check and subject: the same finding on the same day has the same id.</summary>
    private static Guid FindingId(DateOnly runOn, CreditReconciliationFinding f)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"credit-reconciliation|{runOn:yyyy-MM-dd}|{f.CompanyId:N}|{f.Check}|{f.SubjectId:N}"));
        hash[7] = (byte)((hash[7] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }
}
