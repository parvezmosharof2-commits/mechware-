using System.Data;
using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Configurations;

namespace Mechware.Infrastructure.Credit;

/// <summary>
/// ADR-0010 §9-§10 (B3.8, threat model CS27). Reads only: no ledger line is written and NO row is locked (not even FOR SHARE), so a
/// read never queues a verification, a bounce or a placement. Every answer comes from one REPEATABLE READ snapshot, so a total and
/// the lines it adds up are the same moment. The one write is the audit row of a CSV export.
/// </summary>
public sealed class CreditReadService(MechwareDbContext db, ICurrentUserService user, CreditOptions options, TimeProvider clock) : ICreditReadService
{
    public const int MaxPageSize = 200;
    public const int MaxCsvRows = 20_000;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<CreditPage<CreditPaymentListItem>> ListPaymentsAsync(string? status, Guid? companyId, int page, int pageSize, CancellationToken ct = default) =>
        SnapshotAsync(async () =>
        {
            var wanted = ParseStatus<CreditPaymentStatus>(status, "recorded");
            (page, pageSize) = Clamp(page, pageSize);
            var query = from p in db.CreditPayments.AsNoTracking()
                        join c in db.Companies.AsNoTracking() on p.CompanyId equals c.Id
                        where (wanted == null || p.Status == wanted) && (companyId == null || p.CompanyId == companyId)
                        select new { p, c.LegalName };
            var total = await query.CountAsync(ct);
            // The queue is worked oldest first; a history list shows the newest first.
            var ordered = wanted == CreditPaymentStatus.Recorded
                ? query.OrderBy(x => x.p.CreatedAtUtc).ThenBy(x => x.p.Id)
                : query.OrderByDescending(x => x.p.CreatedAtUtc).ThenBy(x => x.p.Id);
            var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var today = Today;
            return new CreditPage<CreditPaymentListItem>(
                rows.Select(r => new CreditPaymentListItem(CreditPaymentService.ToDto(r.p), r.LegalName, r.p.LateEntryReason is not null, r.p.ChequeDate > today)).ToList(),
                page, pageSize, total);
        }, ct);

    public Task<CreditPaymentDetail> GetPaymentAsync(Guid paymentId, CancellationToken ct = default) =>
        SnapshotAsync(async () =>
        {
            var row = await (from p in db.CreditPayments.AsNoTracking()
                             join c in db.Companies.AsNoTracking() on p.CompanyId equals c.Id
                             where p.Id == paymentId
                             select new { p, c.LegalName }).SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("Payment not found.");
            var lines = await LinesAsync(await db.CreditLedgerEntries.AsNoTracking().Where(l => l.PaymentId == paymentId).OrderBy(l => l.CompanyId).ThenBy(l => l.Seq).ToListAsync(ct), ct);
            return new CreditPaymentDetail(CreditPaymentService.ToDto(row.p), row.LegalName, row.p.LateEntryReason is not null, row.p.ChequeDate > Today, lines);
        }, ct);

    public Task<CreditPage<CreditAdjustmentListItem>> ListAdjustmentsAsync(string? status, Guid? companyId, int page, int pageSize, CancellationToken ct = default) =>
        SnapshotAsync(async () =>
        {
            var wanted = ParseStatus<CreditAdjustmentStatus>(status, "requested");
            (page, pageSize) = Clamp(page, pageSize);
            var query = from a in db.CreditAdjustments.AsNoTracking()
                        join c in db.Companies.AsNoTracking() on a.CompanyId equals c.Id
                        where (wanted == null || a.Status == wanted) && (companyId == null || a.CompanyId == companyId)
                        select new { a, c.LegalName };
            var total = await query.CountAsync(ct);
            var ordered = wanted == CreditAdjustmentStatus.Requested
                ? query.OrderBy(x => x.a.CreatedAtUtc).ThenBy(x => x.a.Id)
                : query.OrderByDescending(x => x.a.CreatedAtUtc).ThenBy(x => x.a.Id);
            var rows = await ordered.Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return new CreditPage<CreditAdjustmentListItem>(rows.Select(r => new CreditAdjustmentListItem(CreditAdjustmentService.ToDto(r.a), r.LegalName)).ToList(), page, pageSize, total);
        }, ct);

    public Task<CreditAdjustmentDetail> GetAdjustmentAsync(Guid adjustmentId, CancellationToken ct = default) =>
        SnapshotAsync(async () =>
        {
            var row = await (from a in db.CreditAdjustments.AsNoTracking()
                             join c in db.Companies.AsNoTracking() on a.CompanyId equals c.Id
                             where a.Id == adjustmentId
                             select new { a, c.LegalName }).SingleOrDefaultAsync(ct)
                ?? throw new KeyNotFoundException("Adjustment not found.");
            var lines = await LinesAsync(await db.CreditLedgerEntries.AsNoTracking().Where(l => l.AdjustmentId == adjustmentId).OrderBy(l => l.Seq).ToListAsync(ct), ct);
            return new CreditAdjustmentDetail(CreditAdjustmentService.ToDto(row.a), row.LegalName, lines);
        }, ct);

    public Task<CreditAgingReport> AgingAsync(Guid? companyId, string? q, bool overdueOnly, int page, int pageSize, CancellationToken ct = default) =>
        SnapshotAsync(async () =>
        {
            (page, pageSize) = Clamp(page, pageSize);
            var (today, rows) = await AgingRowsAsync(companyId, q, overdueOnly, ct);
            var totals = rows.Aggregate(default(CreditAgingBuckets), (sum, r) => sum.Plus(r.Buckets));
            return new CreditAgingReport(today, new CreditPage<CreditAgingRow>(rows.Skip((page - 1) * pageSize).Take(pageSize).ToList(), page, pageSize, rows.Count),
                totals, rows.Sum(r => r.ReceivableMinor), rows.Sum(r => r.OverdueMinor));
        }, ct);

    public Task<CreditStatementDto> StatementAsync(Guid companyId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct = default) =>
        SnapshotAsync(async () =>
        {
            (page, pageSize) = Clamp(page, pageSize);
            var (company, query) = await StatementQueryAsync(companyId, from, to, ct);
            var total = await query.CountAsync(ct);
            var rows = await query.OrderBy(l => l.Seq).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            return new CreditStatementDto(company.Id, company.LegalName, from, to, company.CreditUsedMinor, company.CreditOnAccountMinor,
                new CreditPage<CreditLedgerLineDto>(await LinesAsync(rows, ct), page, pageSize, total));
        }, ct);

    public async Task<CreditCsvExport> StatementCsvAsync(Guid companyId, DateOnly? from, DateOnly? to, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, clock.GetUtcNow().UtcDateTime);
        var lines = await SnapshotAsync(async () =>
        {
            var (_, query) = await StatementQueryAsync(companyId, from, to, ct);
            if (await query.CountAsync(ct) > MaxCsvRows)
                throw new ValidationException("from", $"More than {MaxCsvRows} lines: narrow the dates.");
            return await LinesAsync(await query.OrderBy(l => l.Seq).ToListAsync(ct), ct);
        }, ct);

        var rows = new List<string>
        {
            Header("seq", "occurred_on", "posted_at_utc", "kind", "order_number", "payment_method", "payment_reference", "adjustment_id", "due_on",
                "receivable_change_minor", "receivable_change_bdt", "on_account_change_minor", "on_account_change_bdt", "receivable_after_minor", "receivable_after_bdt",
                "on_account_after_minor", "on_account_after_bdt", "actor_user_id", "reason")
        };
        rows.AddRange(lines.Select(l => CreditCsv.Row([
            CreditCsv.Text(l.Seq.ToString(CultureInfo.InvariantCulture)), CreditCsv.Text(Day(l.OccurredOn)), CreditCsv.Text(l.CreatedAtUtc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)),
            CreditCsv.Text(l.Kind), CreditCsv.Text(l.OrderNumber), CreditCsv.Text(l.PaymentMethod), CreditCsv.Text(l.PaymentReference), CreditCsv.Text(l.AdjustmentId?.ToString()),
            CreditCsv.Text(l.DueOn is { } due ? Day(due) : null), Money(l.ReceivableDeltaMinor), Money(l.OnAccountDeltaMinor), Money(l.ReceivableAfterMinor),
            Money(l.OnAccountAfterMinor), CreditCsv.Text(l.ActorUserId?.ToString()), CreditCsv.Text(l.Reason)])));

        await AuditExportAsync("credit.statement_exported", "companies", companyId.ToString(), new { companyId, from, to, rows = lines.Count }, actor, now, ct);
        return new CreditCsvExport($"credit-statement-{companyId:N}.csv", CreditCsv.Document(rows), lines.Count);
    }

    public async Task<CreditCsvExport> AgingCsvAsync(bool overdueOnly, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, clock.GetUtcNow().UtcDateTime);
        var (today, aging) = await SnapshotAsync(() => AgingRowsAsync(null, null, overdueOnly, ct), ct);
        var rows = new List<string>
        {
            Header("company_id", "company_name", "open_orders", "receivable_minor", "receivable_bdt", "current_minor", "current_bdt", "days_1_30_minor", "days_1_30_bdt",
                "days_31_60_minor", "days_31_60_bdt", "days_61_90_minor", "days_61_90_bdt", "days_over_90_minor", "days_over_90_bdt", "overdue_minor", "overdue_bdt",
                "oldest_due_on", "max_days_past_due", "on_account_minor", "on_account_bdt", "on_hold")
        };
        rows.AddRange(aging.Select(r => CreditCsv.Row([
            CreditCsv.Text(r.CompanyId.ToString()), CreditCsv.Text(r.CompanyName), CreditCsv.Minor(r.OpenOrders), Money(r.ReceivableMinor), Money(r.Buckets.Current),
            Money(r.Buckets.Days1To30), Money(r.Buckets.Days31To60), Money(r.Buckets.Days61To90), Money(r.Buckets.Days90Plus), Money(r.OverdueMinor),
            CreditCsv.Text(Day(r.OldestDueOn)), CreditCsv.Minor(r.MaxDaysPastDue), Money(r.OnAccountMinor), CreditCsv.Text(r.OnHold ? "yes" : "no")])));

        await AuditExportAsync("credit.aging_exported", "credit_aging", "all", new { asOf = today, overdueOnly, rows = aging.Count }, actor, now, ct);
        return new CreditCsvExport($"credit-aging-{today:yyyyMMdd}.csv", CreditCsv.Document(rows), aging.Count);
    }

    /// <summary>Open receivables per company, most overdue first (the order of the report and the CSV). One query for the lines, one for the companies, same snapshot.</summary>
    private async Task<(DateOnly Today, List<CreditAgingRow> Rows)> AgingRowsAsync(Guid? companyId, string? q, bool overdueOnly, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var today = CreditCalendar.DhakaDate(now);
        var open = await db.CreditLedgerEntries.AsNoTracking().Where(l => l.OrderId != null && (companyId == null || l.CompanyId == companyId))
            .GroupBy(l => new { l.CompanyId, l.OrderId }).Where(g => g.Sum(l => l.ReceivableDeltaMinor) > 0)
            .Select(g => new { g.Key.CompanyId, Receivable = g.Sum(l => l.ReceivableDeltaMinor), DueOn = g.Max(l => l.DueOn) }).ToListAsync(ct);
        var ids = open.Select(o => o.CompanyId).Distinct().ToList();
        var companies = await db.Companies.AsNoTracking().Where(c => ids.Contains(c.Id) && (c.Status == CompanyStatus.Active || c.Status == CompanyStatus.Suspended))
            .Select(c => new { c.Id, c.LegalName, c.CreditOnAccountMinor, c.CreditHoldOverrideUntilUtc }).ToDictionaryAsync(c => c.Id, ct);
        var holdBefore = options.HoldDueBefore(now);

        var rows = new List<CreditAgingRow>();
        foreach (var group in open.GroupBy(o => o.CompanyId))
        {
            if (!companies.TryGetValue(group.Key, out var company)) continue;
            if (q?.Trim() is { Length: > 0 } term && !company.LegalName.Contains(term, StringComparison.OrdinalIgnoreCase)) continue;
            var buckets = group.Aggregate(default(CreditAgingBuckets), (sum, o) => sum.Plus(CreditAging.Of(o.DueOn!.Value, o.Receivable, today)));
            if (overdueOnly && buckets.Overdue == 0) continue;
            var oldest = group.Min(o => o.DueOn!.Value);
            var held = holdBefore is { } before && oldest < before && !(company.CreditHoldOverrideUntilUtc > now);   // the same rule as the hold (B3.7)
            rows.Add(new CreditAgingRow(company.Id, company.LegalName, group.Count(), buckets.Total, buckets.Overdue, buckets, oldest,
                Math.Max(0, CreditAging.DaysPastDue(oldest, today)), company.CreditOnAccountMinor, held, company.CreditHoldOverrideUntilUtc));
        }

        return (today, rows.OrderByDescending(r => r.OverdueMinor).ThenByDescending(r => r.ReceivableMinor).ThenBy(r => r.CompanyName, StringComparer.Ordinal).ThenBy(r => r.CompanyId).ToList());
    }

    private async Task<(Company Company, IQueryable<CreditLedgerEntry> Query)> StatementQueryAsync(Guid companyId, DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        if (from > to) throw new ValidationException("from", "The start day is after the end day.");
        // A suspended company still has a statement; unknown, pending and rejected companies look the same.
        var company = await db.Companies.AsNoTracking().SingleOrDefaultAsync(c => c.Id == companyId && (c.Status == CompanyStatus.Active || c.Status == CompanyStatus.Suspended), ct)
            ?? throw new KeyNotFoundException("Company not found.");
        return (company, db.CreditLedgerEntries.AsNoTracking()
            .Where(l => l.CompanyId == companyId && (from == null || l.OccurredOn >= from) && (to == null || l.OccurredOn <= to)));
    }

    private async Task<List<CreditLedgerLineDto>> LinesAsync(List<CreditLedgerEntry> rows, CancellationToken ct)
    {
        var orderIds = rows.Where(r => r.OrderId is not null).Select(r => r.OrderId!.Value).Distinct().ToList();
        var paymentIds = rows.Where(r => r.PaymentId is not null).Select(r => r.PaymentId!.Value).Distinct().ToList();
        var numbers = await db.Orders.AsNoTracking().Where(o => orderIds.Contains(o.Id)).Select(o => new { o.Id, o.OrderNumber }).ToDictionaryAsync(o => o.Id, o => o.OrderNumber, ct);
        var payments = await db.CreditPayments.AsNoTracking().Where(p => paymentIds.Contains(p.Id)).Select(p => new { p.Id, p.Method, p.Reference }).ToDictionaryAsync(p => p.Id, ct);
        return rows.Select(l => new CreditLedgerLineDto(l.Seq, l.EventId, Wire(l.Kind), l.OccurredOn, l.CreatedAtUtc, l.OrderId,
            l.OrderId is { } o ? numbers.GetValueOrDefault(o) : null, l.PaymentId,
            l.PaymentId is { } p && payments.TryGetValue(p, out var pay) ? Wire(pay.Method) : null,
            l.PaymentId is { } p2 && payments.TryGetValue(p2, out var pay2) ? pay2.Reference : null, l.AdjustmentId, l.CreditNoteId,
            l.ReceivableDeltaMinor, l.OnAccountDeltaMinor, l.ReceivableAfterMinor, l.OnAccountAfterMinor, l.DueOn, l.ActorUserId, l.Reason)).ToList();
    }

    /// <summary>One consistent moment for several queries, without a single lock (MVCC): the totals and the lines they add up never differ.</summary>
    private async Task<T> SnapshotAsync<T>(Func<Task<T>> read, CancellationToken ct)
    {
        if (db.Database.CurrentTransaction is not null) return await read();
        await using var tx = await db.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, ct);
        var result = await read();
        await tx.CommitAsync(ct);
        return result;
    }

    private async Task AuditExportAsync(string action, string entity, string entityId, object details, Guid actor, DateTime now, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, user.IpAddress, action, entity, entityId, null, JsonSerializer.Serialize(details, Json), user.CorrelationId, now));
        await db.SaveChangesAsync(ct);
    }

    private static TEnum? ParseStatus<TEnum>(string? status, string fallback) where TEnum : struct, Enum
    {
        var text = string.IsNullOrWhiteSpace(status) ? fallback : status.Trim();
        if (text.Equals("all", StringComparison.OrdinalIgnoreCase)) return null;
        // Enum.TryParse also takes numbers; only the names are part of the API.
        if (text.All(char.IsLetter) && Enum.TryParse<TEnum>(text, true, out var parsed) && Enum.IsDefined(parsed)) return parsed;
        throw new ValidationException("status", $"Use one of: {string.Join(", ", Enum.GetNames<TEnum>().Select(n => n.ToLowerInvariant()))}, all.");
    }

    private static (int Page, int PageSize) Clamp(int page, int pageSize) => (Math.Max(1, page), Math.Clamp(pageSize, 1, MaxPageSize));

    private static string Header(params string[] names) => CreditCsv.Row(names.Select(CreditCsv.Text).ToArray());

    private static string Money(long poisha) => CreditCsv.Minor(poisha) + "," + CreditCsv.Bdt(poisha);

    private static string Day(DateOnly day) => day.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    private static string Wire<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToSnake(value.ToString());

    private DateOnly Today => CreditCalendar.DhakaDate(clock.GetUtcNow().UtcDateTime);

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
}
