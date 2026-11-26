using System.Data;
using System.Diagnostics.Metrics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Mechware.Application.Invoicing;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;

namespace Mechware.Infrastructure.Invoicing;

/// <summary>
/// E6.3d (ADR-0012 §17, threat model IT13, IT16). One transaction: <c>REPEATABLE READ</c>, first statement a try of the advisory lock
/// (busy = another pass runs: skip), then every read in that one snapshot with no row lock, the checks, and the inserts of findings not yet
/// recorded today (audit + outbox only; ids from day, check and subject, so a finding is recorded once a day and again each night while
/// the damage lasts). Never repairs. Checks:
/// <list type="bullet">
/// <item><see cref="NumberGap"/>/<see cref="NumberDuplicate"/>: per seller, series and fiscal year (every year, the first included) the
/// serials are exactly 1…max; <see cref="CounterMismatch"/>: the counter's year ends at <c>next − 1</c> and no later year exists;</item>
/// <item><see cref="ContentHash"/>: the hash recomputed from the stored snapshot and lines equals the stored one;</item>
/// <item><see cref="MissingInvoice"/>/<see cref="DuplicateInvoice"/>: only when this host runs Live, every sub-order of every shipment
/// since the first shipment with a Live 6.3 has exactly one Live 6.3 of its seller (earlier shipments were Off or Specimen: ignored);</item>
/// <item><see cref="InvoiceTotal"/>: a 6.3 equals its sub-order to the poisha (total, goods net, goods VAT); <see cref="LinesTotal"/>: the
/// header equals the sum of its lines;</item>
/// <item>credit caps: no credit notes exist before E6.5, so <see cref="CreditCaps"/> finds nothing yet; E6.5a/b extend it.</item>
/// </list>
/// </summary>
public sealed class InvoiceReconciliationService(MechwareDbContext db, InvoicingOptions options, ILogger<InvoiceReconciliationService> logger)
    : IInvoiceReconciliationService
{
    public const string FindingEvent = "invoice.reconciliation_finding";
    public const string NumberGap = "number_gap", NumberDuplicate = "number_duplicate", CounterMismatch = "counter_mismatch",
        ContentHash = "content_hash", MissingInvoice = "missing_invoice", DuplicateInvoice = "duplicate_invoice",
        InvoiceTotal = "invoice_total", LinesTotal = "invoice_lines_total";
    private static readonly Meter Meter = new("Mechware.Invoicing");
    private static readonly Counter<long> Recorded = Meter.CreateCounter<long>("invoice_reconciliation_findings", description: "Invoice reconciliation findings recorded");
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record Sub(Guid Id, Guid OrderId, Guid SellerId, long GoodsNet, long Tax, long Total);
    private sealed record ShipmentRow(Guid Id, Guid OrderId);

    public async Task<InvoiceReconciliationReport> RunAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var runOn = FiscalYear.DhakaDate(nowUtc);
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var locked = (await db.Database.SqlQueryRaw<bool>(
            "SELECT pg_try_advisory_xact_lock(hashtextextended('invoice-reconciliation', 0)) AS \"Value\"").ToListAsync(ct)).Single();
        if (!locked) return new InvoiceReconciliationReport(runOn, false, 0, [], 0);

        var (invoices, counters, subs, shipped) = await ReadSnapshot.RunAsync(db, () => ReadAsync(ct), ct);
        var findings = Numbers(invoices, counters).Concat(Hashes(invoices)).Concat(Totals(invoices, subs)).Concat(CreditCaps(invoices))
            .Concat(Missing(invoices, subs, shipped)).ToList();
        foreach (var f in findings)
            logger.LogError("ALERT invoice reconciliation: {Check} on {EntityType} {EntityId} ({Detail}): expected {Expected}, found {Actual}",
                f.Check, f.EntityType, f.EntityId, f.Detail, f.Expected, f.Actual);
        var fresh = await RecordAsync(runOn, findings, nowUtc, ct);
        if (fresh < 0) await tx.RollbackAsync(ct);   // a parallel pass recorded today's findings first
        else await tx.CommitAsync(ct);
        fresh = Math.Max(0, fresh);
        if (fresh > 0) Recorded.Add(fresh);
        return new InvoiceReconciliationReport(runOn, true, invoices.Count, findings, fresh);
    }

    private async Task<(List<Invoice>, List<InvoiceNumberCounter>, Dictionary<Guid, Sub>, List<ShipmentRow>)> ReadAsync(CancellationToken ct)
    {
        var invoices = await db.Invoices.AsNoTracking().Include(i => i.Lines).AsSplitQuery().ToListAsync(ct);
        var counters = await db.InvoiceNumberCounters.AsNoTracking().ToListAsync(ct);
        List<ShipmentRow> shipped = [];
        if (options.EffectiveMode == InvoicingMode.Live)
        {
            var since = await db.Set<Shipment>().Where(s => db.Invoices.Any(i => i.ShipmentId == s.Id && i.Mode == InvoicingMode.Live && i.Kind == InvoiceKind.TaxInvoice))
                .MinAsync(s => (DateTime?)s.ShippedAtUtc, ct);
            shipped = since is null ? [] : await db.Set<Shipment>().Where(s => s.ShippedAtUtc >= since).Select(s => new ShipmentRow(s.Id, s.OrderId)).ToListAsync(ct);
        }

        var subIds = invoices.Select(i => i.SubOrderId).ToHashSet();
        var orderIds = shipped.Select(s => s.OrderId).ToHashSet();
        var subs = await db.SubOrders.AsNoTracking().Where(s => subIds.Contains(s.Id) || orderIds.Contains(s.OrderId))
            .Select(s => new Sub(s.Id, s.OrderId, s.SellerId, s.GoodsNetMinor, s.TaxMinor, s.TotalMinor)).ToListAsync(ct);
        return (invoices, counters, subs.ToDictionary(s => s.Id), shipped);
    }

    private static IEnumerable<InvoiceReconciliationFinding> Numbers(List<Invoice> invoices, List<InvoiceNumberCounter> counters)
    {
        foreach (var year in invoices.GroupBy(i => (i.SellerId, i.Series, i.FiscalYear)))
        {
            var detail = $"{year.Key.Series} {FiscalYear.Label(year.Key.FiscalYear)}";
            var distinct = year.Select(i => i.Serial).Distinct().LongCount();
            if (distinct != year.LongCount()) yield return new(NumberDuplicate, "sellers", year.Key.SellerId, detail, distinct, year.LongCount());
            var max = year.Max(i => i.Serial);
            if (distinct != max) yield return new(NumberGap, "sellers", year.Key.SellerId, detail, max, distinct);   // serials ≥ 1 (CHECK): 1…max
        }

        foreach (var (seller, series) in invoices.Select(i => (i.SellerId, i.Series)).Union(counters.Select(c => (c.SellerId, c.Series))).Distinct())
        {
            var c = counters.FirstOrDefault(x => x.SellerId == seller && x.Series == series);
            var mine = invoices.Where(i => i.SellerId == seller && i.Series == series).ToList();
            var last = c is null ? 0 : mine.Where(i => i.FiscalYear == c.Year).Select(i => i.Serial).DefaultIfEmpty().Max();
            if (c is null || last != c.NextSerial - 1 || mine.Any(i => i.FiscalYear > c.Year))
                yield return new(CounterMismatch, "sellers", seller, series.ToString(), c is null ? 0 : c.NextSerial - 1, last);
        }
    }

    private static IEnumerable<InvoiceReconciliationFinding> Hashes(List<Invoice> invoices) =>
        invoices.Where(i => !InvoiceContentHash.Matches(i, i.ContentHash)).Select(i => new InvoiceReconciliationFinding(ContentHash, "invoices", i.Id, i.Number, 0, 1));

    private static IEnumerable<InvoiceReconciliationFinding> Totals(List<Invoice> invoices, Dictionary<Guid, Sub> subs)
    {
        foreach (var i in invoices)
        {
            if (i.NetMinor != i.Lines.Sum(l => l.NetMinor) || i.SdMinor != i.Lines.Sum(l => l.SdMinor) || i.VatMinor != i.Lines.Sum(l => l.VatMinor)
                || i.TotalMinor != i.Lines.Sum(l => l.TotalMinor))
                yield return new(LinesTotal, "invoices", i.Id, i.Number, i.Lines.Sum(l => l.TotalMinor), i.TotalMinor);
            if (i.Kind != InvoiceKind.TaxInvoice) continue;
            var goods = i.Lines.Where(l => l.Kind == InvoiceLineKind.Goods).ToList();
            if (!subs.TryGetValue(i.SubOrderId, out var s) || s.Total != i.TotalMinor || s.GoodsNet != goods.Sum(l => l.NetMinor) || s.Tax != goods.Sum(l => l.VatMinor))
                yield return new(InvoiceTotal, "invoices", i.Id, i.Number, s?.Total ?? 0, i.TotalMinor);
        }
    }

    /// <summary>
    /// Σ credit notes ≤ the original per line and per invoice (ADR §9, §17). No credit note can exist before E6.5 (the domain, the API
    /// and the deferred cap trigger come there), so this finds nothing yet; E6.5a/b add the check here, E6.6 the withholding caps.
    /// </summary>
    private static IEnumerable<InvoiceReconciliationFinding> CreditCaps(List<Invoice> invoices) => [];

    private static IEnumerable<InvoiceReconciliationFinding> Missing(List<Invoice> invoices, Dictionary<Guid, Sub> subs, List<ShipmentRow> shipped)
    {
        var live = invoices.Where(i => i.Kind == InvoiceKind.TaxInvoice && i.Mode == InvoicingMode.Live).ToLookup(i => (i.ShipmentId, i.SellerId));
        var byOrder = subs.Values.ToLookup(s => s.OrderId);
        foreach (var shipment in shipped)
            foreach (var sub in byOrder[shipment.OrderId])
            {
                var n = live[(shipment.Id, sub.SellerId)].Count();
                if (n != 1) yield return new(n == 0 ? MissingInvoice : DuplicateInvoice, "sub_orders", sub.Id, $"order {sub.OrderId:D}", 1, n);
            }
    }

    /// <summary>One audit row + one outbox event per finding not yet recorded on <paramref name="runOn"/>, inside the pass's transaction; −1 = lost a race.</summary>
    private async Task<int> RecordAsync(DateOnly runOn, List<InvoiceReconciliationFinding> findings, DateTime now, CancellationToken ct)
    {
        if (findings.Count == 0) return 0;
        var keyed = findings.Select(f => (Id: FindingId(runOn, f), Finding: f)).DistinctBy(x => x.Id).ToList();
        var ids = keyed.Select(x => x.Id).ToList();
        var done = (await db.AuditLogs.AsNoTracking().Where(a => ids.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct)).ToHashSet();
        var fresh = keyed.Where(x => !done.Contains(x.Id)).ToList();
        foreach (var (id, f) in fresh)
        {
            db.AuditLogs.Add(new AuditLog(id, null, null, FindingEvent, f.EntityType, f.EntityId.ToString(), null,
                JsonSerializer.Serialize(new { EventId = id, RunOn = runOn, f.Check, f.Detail, f.Expected, f.Actual }, Json), null, now));
            db.OutboxMessages.Add(new OutboxMessage
            {
                Id = id,
                EventType = FindingEvent,
                AggregateType = nameof(Invoice),
                AggregateId = f.EntityId,
                OccurredAtUtc = now,
                Payload = JsonSerializer.Serialize(new { EventId = id, RunOn = runOn, f.Check, f.EntityType, f.EntityId, f.Detail }, Json)
            });
        }

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException e) when (e.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            // Only when a pass that committed between this snapshot and the lock recorded the same day's findings first: nothing is lost.
            logger.LogWarning("Invoice reconciliation {RunOn}: findings were recorded by a parallel pass.", runOn);
            return -1;
        }

        return fresh.Count;
    }

    /// <summary>A UUID (version 8) of SHA-256 over the day, check and subject: the same finding on the same day has the same id.</summary>
    private static Guid FindingId(DateOnly runOn, InvoiceReconciliationFinding f)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"invoice-reconciliation|{runOn:yyyy-MM-dd}|{f.Check}|{f.EntityType}|{f.EntityId:N}|{f.Detail}"));
        hash[7] = (byte)((hash[7] & 0x0F) | 0x80);
        hash[8] = (byte)((hash[8] & 0x3F) | 0x80);
        return new Guid(hash.AsSpan(0, 16));
    }
}
