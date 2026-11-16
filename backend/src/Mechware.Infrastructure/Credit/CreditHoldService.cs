using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;

namespace Mechware.Infrastructure.Credit;

/// <summary>
/// ADR-0010 §9 (B3.7, threat model CS25). Both commands lock only the <c>companies</c> row (FOR UPDATE, the lock every ledger writer and
/// every credit placement takes), so they fit the global lock order and serialise with placements, verifications and bounces of that
/// company. They write no ledger line: the hold is derived from the ledger (<see cref="CreditLedgerSql.OverdueSinceAsync"/>).
/// </summary>
public sealed class CreditHoldService(MechwareDbContext db, ICurrentUserService user, CreditOptions options, TimeProvider clock) : ICreditHoldService
{
    public const string StartedEvent = "credit.hold_started";
    public const string EndedEvent = "credit.hold_ended";
    public const string OverriddenEvent = "credit.hold_overridden";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CreditHoldDto> OverrideAsync(Guid companyId, OverrideCreditHoldRequest request, CancellationToken ct = default)
    {
        var actor = user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        var now = clock.GetUtcNow().UtcDateTime;
        if (request.Days is not { } days) throw new ValidationException("days", "Give the number of days (0 ends a lift).");
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        // A suspended company still owes and may still be lifted; unknown, pending and rejected companies look the same.
        var company = await LockAsync(companyId, ct) is { Status: CompanyStatus.Active or CompanyStatus.Suspended } c
            ? c : throw new KeyNotFoundException("Company not found.");
        var before = company.CreditHoldOverrideUntilUtc;
        var reason = company.LiftCreditHold(days, options.HoldOverrideMaxDays, request.Reason, now);
        var overdue = await CreditLedgerSql.OverdueSinceAsync(db, companyId, options.HoldDueBefore(now), ct);

        var eventId = Guid.NewGuid();
        var after = new { EventId = eventId, Days = days, company.CreditHoldOverrideUntilUtc, Reason = reason, OverdueSince = overdue };
        Record("credit.hold_overridden", OverriddenEvent, company, actor, new { CreditHoldOverrideUntilUtc = before }, after, eventId, now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return Dto(company, overdue, now);
    }

    public async Task<int> SweepAsync(DateTime nowUtc, int batchSize = 200, CancellationToken ct = default)
    {
        var dueBefore = options.HoldDueBefore(nowUtc) ?? DateOnly.MinValue;   // no hold configured: every noted hold ends
        // The companies whose noted hold differs from the ledger, read without locks; each is decided again under its own lock.
        var ids = await db.Database.SqlQuery<Guid>($"""
            SELECT co.id AS "Value" FROM companies co
             WHERE co.credit_ledger_seq > 0 AND (co.credit_hold_since IS NULL) = EXISTS (
                   SELECT 1 FROM credit_ledger_entries c
                    WHERE c.company_id = co.id AND c.kind = 'charge' AND c.due_on < {dueBefore}
                      AND (SELECT sum(e.receivable_delta_minor) FROM credit_ledger_entries e WHERE e.company_id = co.id AND e.order_id = c.order_id) > 0)
             ORDER BY co.id LIMIT {batchSize}
            """).ToListAsync(ct);

        var changed = 0;
        foreach (var id in ids)
        {
            db.ChangeTracker.Clear();
            await using var tx = await db.Database.BeginTransactionAsync(ct);
            var company = await LockAsync(id, ct);
            var overdue = await CreditLedgerSql.OverdueSinceAsync(db, id, options.HoldDueBefore(nowUtc), ct);
            if (company is null || !company.NoteCreditHold(overdue is not null, nowUtc)) continue;   // a parallel pass noted it first

            var eventId = Guid.NewGuid();
            var started = overdue is not null;
            Record(started ? StartedEvent : EndedEvent, started ? StartedEvent : EndedEvent, company, null, null,
                new { EventId = eventId, OverdueSince = overdue, options.HoldAfterDaysPastDue, company.CreditUsedMinor, company.CreditHoldOverrideUntilUtc },
                eventId, nowUtc);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            changed++;
        }
        return changed;
    }

    private async Task<Company?> LockAsync(Guid companyId, CancellationToken ct) =>
        (await db.Companies.FromSqlInterpolated($"SELECT c.*, c.xmin FROM companies c WHERE c.id = {companyId} FOR UPDATE").ToListAsync(ct)).SingleOrDefault();

    /// <summary>One audit row and one outbox event (<c>Id</c> = event id) in the caller's transaction (CS29).</summary>
    private void Record(string action, string eventType, Company company, Guid? actor, object? before, object after, Guid eventId, DateTime now)
    {
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, actor is null ? null : user.IpAddress, action, "companies", company.Id.ToString(),
            before is null ? null : JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(after, Json), user.CorrelationId, now));
        db.OutboxMessages.Add(new OutboxMessage
        {
            Id = eventId,
            EventType = eventType,
            AggregateType = nameof(Company),
            AggregateId = company.Id,
            OccurredAtUtc = now,
            Payload = JsonSerializer.Serialize(new { EventId = eventId, CompanyId = company.Id, company.CreditHoldOverrideUntilUtc }, Json)
        });
    }

    private static CreditHoldDto Dto(Company c, DateOnly? overdue, DateTime now) =>
        new(c.Id, overdue, overdue is not null && !c.CreditHoldLifted(now), c.CreditHoldOverrideUntilUtc, c.CreditHoldSinceUtc);
}
