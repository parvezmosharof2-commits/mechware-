using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Support;
using R = Mechware.Domain.Credit.CreditReconciliation;
using Staff = Mechware.IntegrationTests.Credit.CreditReconciliationServiceTests.Staff;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// B3.10, threat model §১২ CS28: damage that the database guards would refuse is seeded with raw SQL in this class's own throw-away
/// database, with every trigger (append-only, chain, settlement guard, foreign keys) switched off for that one transaction
/// (<c>session_replication_role = replica</c>). The pass must name each damage, record it once per day (audit row + outbox event, also
/// with parallel passes) and change nothing it read.
/// </summary>
public class CreditReconciliationCorruptionTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _binSeq = 4_200_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private MechwareDbContext Ctx() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private sealed record Team(Guid Company, Guid Admin);

    private async Task<Team> TeamAsync()
    {
        var admin = await TestUsers.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Damaged Books Ltd", "TL-B310-2",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        return new Team(company, admin);
    }

    private async Task<Guid> ChargedOrderAsync(Team t, long amount, int daysAgo = 2, bool confirmedAt = true)
    {
        await using var db = fixture.CreateContext();
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, amount, confirmedAtUtc: confirmedAt ? at : null);
        var company = await db.Companies.SingleAsync(c => c.Id == t.Company);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == t.Company).ToListAsync();
        db.CreditLedgerEntries.Add(new CreditLedger(company, lines).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<Guid> RecordedAsync(Guid company, long amount)
    {
        await using var db = Ctx();
        var service = new CreditPaymentService(db, new Staff(await TestUsers.CreateAsync(fixture)), new CreditOptions(), TimeProvider.System);
        return (await service.RecordAsync(company, new RecordCreditPaymentRequest("rtgs", amount, Today, "TRX" + Guid.NewGuid().ToString("N")[..12]), Key())).Payment.Id;
    }

    private async Task<Guid> PaidAsync(Guid company, long amount)
    {
        var payment = await RecordedAsync(company, amount);
        await using var db = Ctx();
        await new CreditPaymentService(db, new Staff(await TestUsers.CreateAsync(fixture)), new CreditOptions(), TimeProvider.System)
            .VerifyAsync(payment, new VerifyCreditPaymentRequest(Today));
        return payment;
    }

    /// <summary>Raw SQL with every trigger and foreign key off, for this transaction only (needs a superuser, as the test server has).</summary>
    private async Task DamageAsync(string sql, params object[] args)
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var tx = await conn.BeginTransactionAsync();
        await using (var off = new NpgsqlCommand("SET LOCAL session_replication_role = replica", conn, tx)) await off.ExecuteNonQueryAsync();
        await using (var cmd = new NpgsqlCommand(sql, conn, tx))
        {
            for (var i = 0; i < args.Length; i++) cmd.Parameters.AddWithValue("p" + i, args[i]);
            await cmd.ExecuteNonQueryAsync();
        }
        await tx.CommitAsync();
    }

    private async Task<CreditReconciliationReport> ReconcileAsync(DateTime now)
    {
        await using var db = Ctx();
        return await new CreditReconciliationService(db, new CreditOptions(), batchSize: 3).RunAsync(now);   // several batches
    }

    /// <summary>A fingerprint of every money row the reconciliation reads.</summary>
    private async Task<string> BooksFingerprintAsync()
    {
        await using var conn = new NpgsqlConnection(fixture.ConnectionString);
        await conn.OpenAsync();
        await using var cmd = new NpgsqlCommand("""
            SELECT concat_ws('#',
                (SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM credit_ledger_entries t),
                (SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM credit_payments t),
                (SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM credit_adjustments t),
                (SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM companies t),
                (SELECT md5(string_agg(t::text, '|' ORDER BY t.id)) FROM orders t))
            """, conn);
        return (string)(await cmd.ExecuteScalarAsync())!;
    }

    [PostgresFact]
    public async Task Reconciliation_FindsAMismatch_AndAlerts_WithoutRepairing()
    {
        var healthy = await TeamAsync();
        await ChargedOrderAsync(healthy, 10_000);
        await PaidAsync(healthy.Company, 15_000);

        // 1. credit_used moved without a ledger line.
        var used = await TeamAsync();
        await ChargedOrderAsync(used, 10_000);
        await DamageAsync("UPDATE companies SET credit_used = credit_used + 1 WHERE id = @p0", used.Company);

        // 2. A gap in the chain: line 3 renumbered 13 (and the counter with it).
        var gap = await TeamAsync();
        for (var i = 0; i < 3; i++) await ChargedOrderAsync(gap, 1_000);
        await DamageAsync("UPDATE credit_ledger_entries SET seq = 13 WHERE company_id = @p0 AND seq = 3; UPDATE companies SET credit_ledger_seq = 13 WHERE id = @p0", gap.Company);

        // 3. A payment marked verified without its ledger lines.
        var ghost = await TeamAsync();
        await ChargedOrderAsync(ghost, 10_000);
        var ghostPayment = await RecordedAsync(ghost.Company, 4_000);
        await DamageAsync("UPDATE credit_payments SET status = 'verified', verified_by = @p1, verified_at = now(), cleared_on = current_date WHERE id = @p0",
            ghostPayment, ghost.Admin);

        // 4. A confirmed credit order that was never charged.
        var uncharged = await TeamAsync();
        Guid unchargedOrder;
        Guid preE83Order;   // confirmed before E8.3: no confirmation time, only the Confirmed event
        await using (var db = fixture.CreateContext())
        {
            unchargedOrder = await CreditTestData.InsertOrderAsync(db, uncharged.Admin, uncharged.Company, 8_000, confirmedAtUtc: DateTime.UtcNow);
            preE83Order = await CreditTestData.InsertOrderAsync(db, uncharged.Admin, uncharged.Company, 6_000);
            await CreditTestData.AddEventAsync(db, preE83Order, 2, "PendingPayment", "Confirmed", DateTime.UtcNow);
            // Not confirmed at all (no time, no event) or not on credit: no charge expected.
            await CreditTestData.InsertOrderAsync(db, uncharged.Admin, uncharged.Company, 6_000);
            await CreditTestData.InsertOrderAsync(db, uncharged.Admin, uncharged.Company, 6_000, onCredit: false, confirmedAtUtc: DateTime.UtcNow);
        }

        // 5. A cancelled order whose charge was never reduced.
        var cancelled = await TeamAsync();
        var cancelledOrder = await ChargedOrderAsync(cancelled, 9_000);
        await DamageAsync("UPDATE orders SET status = 'Cancelled' WHERE id = @p0", cancelledOrder);

        // 6. An adjustment marked approved without its line.
        var adjusted = await TeamAsync();
        var adjustedOrder = await ChargedOrderAsync(adjusted, 9_000);
        Guid adjustment;
        await using (var db = Ctx())
            adjustment = (await new CreditAdjustmentService(db, new Staff(await TestUsers.CreateAsync(fixture)), new CreditOptions(), TimeProvider.System)
                .RequestAsync(adjusted.Company, new RequestCreditAdjustmentRequest("write_off", 1_000, "damaged in transit, agreed", OrderId: adjustedOrder), Key())).Adjustment.Id;
        await DamageAsync("UPDATE credit_adjustments SET status = 'approved', approved_by = @p1, approved_at = now() WHERE id = @p0", adjustment, adjusted.Admin);

        // 7. Overdue for weeks, but the hold sweep never noted it. 8. A lift of 30 days.
        var unnoted = await TeamAsync();
        await ChargedOrderAsync(unnoted, 10_000, daysAgo: 50);
        var lifted = await TeamAsync();
        await DamageAsync("UPDATE companies SET credit_hold_override_until = now() + interval '30 days' WHERE id = @p0", lifted.Company);

        // 9. A line reducing an order that does not exist (chain and totals kept right). 10. A line of a company that does not exist.
        var orphan = await TeamAsync();
        await ChargedOrderAsync(orphan, 5_000);
        var missingOrder = Guid.NewGuid();
        await DamageAsync("""
            INSERT INTO credit_ledger_entries (id, company_id, seq, event_id, kind, order_id, receivable_delta_minor, on_account_delta_minor,
                receivable_after_minor, on_account_after_minor, occurred_on, currency, idempotency_key, reason, created_at)
            VALUES (gen_random_uuid(), @p0, 2, gen_random_uuid(), 'charge_reduced', @p1, -1000, 0, 4000, 0, current_date, 'BDT', 'reduce:forged:' || @p1, 'forged', now());
            UPDATE companies SET credit_used = 4000, credit_ledger_seq = 2 WHERE id = @p0
            """, orphan.Company, missingOrder);
        var nowhere = Guid.NewGuid();
        await DamageAsync("""
            INSERT INTO credit_ledger_entries (id, company_id, seq, event_id, kind, order_id, receivable_delta_minor, on_account_delta_minor,
                receivable_after_minor, on_account_after_minor, due_on, occurred_on, currency, idempotency_key, reason, created_at)
            VALUES (gen_random_uuid(), @p0, 1, gen_random_uuid(), 'charge', gen_random_uuid(), 1000, 0, 1000, 0, current_date, current_date, 'BDT', 'charge:forged:' || @p0, 'forged', now())
            """, nowhere);

        // 11. A line of one company reducing another company's order (the other company's own books stay right).
        var borrower = await TeamAsync();
        var lender = await TeamAsync();
        await ChargedOrderAsync(borrower, 5_000);
        var lentOrder = await ChargedOrderAsync(lender, 5_000, confirmedAt: false);   // loaded only because the line names it
        await DamageAsync("""
            INSERT INTO credit_ledger_entries (id, company_id, seq, event_id, kind, order_id, receivable_delta_minor, on_account_delta_minor,
                receivable_after_minor, on_account_after_minor, occurred_on, currency, idempotency_key, reason, created_at)
            VALUES (gen_random_uuid(), @p0, 2, gen_random_uuid(), 'charge_reduced', @p1, -1000, 0, 4000, 0, current_date, 'BDT', 'reduce:borrowed:' || @p1, 'forged', now());
            UPDATE companies SET credit_used = 4000, credit_ledger_seq = 2 WHERE id = @p0
            """, borrower.Company, lentOrder);

        var books = await BooksFingerprintAsync();
        var now = DateTime.UtcNow;
        var report = await ReconcileAsync(now);

        string[] Of(Guid company) => report.Findings.Where(f => f.CompanyId == company).Select(f => f.Check).Order().ToArray();
        Of(healthy.Company).Should().BeEmpty();
        Of(used.Company).Should().Equal(R.CreditUsed);
        report.Findings.Single(f => f.CompanyId == used.Company).Should().Be(new CreditReconciliationFinding(used.Company, R.CreditUsed, null, 10_000, 10_001));
        Of(gap.Company).Should().Equal(R.LedgerChain);
        Of(ghost.Company).Should().Equal(R.PaymentBalance, R.PaymentVerifiedAmount);
        report.Findings.Where(f => f.CompanyId == ghost.Company).Should().OnlyContain(f => f.SubjectId == ghostPayment && f.Expected == 4_000 && f.Actual == 0);
        Of(uncharged.Company).Should().Equal(R.OrderCharge, R.OrderCharge);
        report.Findings.Where(f => f.CompanyId == uncharged.Company).Select(f => f.SubjectId).Should().BeEquivalentTo(new Guid?[] { unchargedOrder, preE83Order });
        Of(cancelled.Company).Should().Equal(R.CancelledOrderNotReleased);
        Of(adjusted.Company).Should().Equal(R.AdjustmentLine);
        Of(unnoted.Company).Should().Equal(R.HoldNote);
        Of(lifted.Company).Should().Equal(R.HoldOverrideTooLong);
        Of(orphan.Company).Should().Equal(R.OrderBalance, R.UnknownRecord);
        Of(nowhere).Should().Equal(R.UnknownCompany);
        Of(borrower.Company).Should().Contain(R.ForeignRecord).And.NotContain(R.UnknownRecord);
        report.Findings.Should().Contain(f => f.CompanyId == borrower.Company && f.Check == R.ForeignRecord && f.SubjectId == lentOrder);
        Of(lender.Company).Should().BeEmpty();
        report.NewlyRecorded.Should().Be(report.Findings.Count);

        // Alerted: one audit row (system actor) and one outbox event per finding, with the same id; nothing else changed.
        (await BooksFingerprintAsync()).Should().Be(books, "the reconciliation never repairs anything");
        await using (var db = fixture.CreateContext())
        {
            var audits = await db.AuditLogs.AsNoTracking().Where(a => a.Action == CreditReconciliationService.MismatchEvent).ToListAsync();
            var events = await db.OutboxMessages.AsNoTracking().Where(m => m.EventType == CreditReconciliationService.MismatchEvent).ToListAsync();
            audits.Should().HaveCount(report.Findings.Count).And.OnlyContain(a => a.ActorId == null && a.ActorIp == null && a.EntityName == "companies");
            events.Select(e => e.Id).Should().BeEquivalentTo(audits.Select(a => a.Id));
            var usedAudit = audits.Single(a => a.EntityId == used.Company.ToString());
            using var details = JsonDocument.Parse(usedAudit.NewValuesJson!);
            (details.RootElement.GetProperty("check").GetString(), details.RootElement.GetProperty("expected").GetInt64(), details.RootElement.GetProperty("actual").GetInt64(),
                details.RootElement.GetProperty("runOn").GetString()).Should().Be((R.CreditUsed, 10_000L, 10_001L, Today.ToString("yyyy-MM-dd")));
            var usedEvent = events.Single(e => e.Id == usedAudit.Id);
            (usedEvent.AggregateId, usedEvent.AggregateType).Should().Be((used.Company, nameof(Company)));
            usedEvent.Payload.Should().NotContain("Damaged Books", "no names in the alert");
        }

        // The same day again, three passes at once: the same findings, nothing recorded twice.
        using var gate = new ManualResetEventSlim(false);
        var again = Enumerable.Range(0, 3).Select(_ => Task.Run(async () => { gate.Wait(); return await ReconcileAsync(now.AddMinutes(5)); })).ToList();
        gate.Set();
        var reports = await Task.WhenAll(again);
        reports.Should().OnlyContain(r => r.NewlyRecorded == 0 && r.Findings.Count == report.Findings.Count);
        // The next night, three passes at once: each finding is alerted again, once.
        gate.Reset();
        var tomorrow = Enumerable.Range(0, 3).Select(_ => Task.Run(async () => { gate.Wait(); return await ReconcileAsync(now.AddDays(1)); })).ToList();
        gate.Set();
        (await Task.WhenAll(tomorrow)).Sum(r => r.NewlyRecorded).Should().Be(report.Findings.Count);
        await using (var db = fixture.CreateContext())
            (await db.AuditLogs.CountAsync(a => a.Action == CreditReconciliationService.MismatchEvent)).Should().Be(2 * report.Findings.Count);
        (await BooksFingerprintAsync()).Should().Be(books);
    }
}
