using System.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// B3.8 (ADR-0010 §9-§10, threat model CS27) against a real database with every ledger guard installed: the aging buckets at their
/// exact edges, the queues and statements, one snapshot per answer, no lock taken by a read, and CSV exports that are audited and safe.
/// </summary>
public class CreditReadServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Ip = "203.0.113.38";
    private static long _binSeq = 3_970_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);
    private static string Ref() => "TRX" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private sealed class Staff(Guid id, string correlationId) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => Ip;
        public string? UserAgent => null;
        public string? CorrelationId => correlationId;
        public bool HasPermission(string permission) => true;
    }

    private MechwareDbContext Ctx() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString).Options);

    private async Task<T> Read<T>(Func<CreditReadService, Task<T>> action, Guid? actor = null, CreditOptions? options = null)
    {
        await using var db = Ctx();
        return await action(new CreditReadService(db, new Staff(actor ?? Guid.NewGuid(), "corr-read"), options ?? new CreditOptions(), TimeProvider.System));
    }

    private async Task<T> Pay<T>(Guid actor, Func<CreditPaymentService, Task<T>> action)
    {
        await using var db = Ctx();
        return await action(new CreditPaymentService(db, new Staff(actor, "corr-pay"), new CreditOptions(), TimeProvider.System));
    }

    private async Task<T> Adj<T>(Guid actor, Func<CreditAdjustmentService, Task<T>> action)
    {
        await using var db = Ctx();
        return await action(new CreditAdjustmentService(db, new Staff(actor, "corr-adj"), new CreditOptions { PayoutAfterClearingDays = 0 }, TimeProvider.System));
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    private async Task<Guid> CompanyAsync(string name, long limit = 1_000_000_000)
    {
        var owner = await UserAsync();
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(owner, new RegisterCompanyRequest(name, "TL-B38-1",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, limit));
        return id;
    }

    /// <summary>An order charged so that it is exactly <paramref name="daysPastDue"/> days past due today (Net-30: charged 30 + n days ago).</summary>
    private async Task<Guid> OrderDueAsync(Guid company, long amount, int daysPastDue)
    {
        await using var db = fixture.CreateContext();
        var order = await CreditTestData.InsertOrderAsync(db, await UserAsync(), company, amount);
        var entity = await db.Companies.SingleAsync(c => c.Id == company);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == company).ToListAsync();
        var at = DateTime.UtcNow.AddDays(-(30 + daysPastDue));
        db.CreditLedgerEntries.Add(new CreditLedger(entity, lines).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return order;
    }

    private async Task<Guid> PaidAsync(Guid company, long amount, string? note = null, string method = "rtgs")
    {
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var request = method == "cheque"
            ? new RecordCreditPaymentRequest(method, amount, Today, "CHQ" + Ref(), "Sonali Bank", ChequeDate: Today, Note: note)
            : new RecordCreditPaymentRequest(method, amount, Today, Ref(), Note: note);
        var payment = (await Pay(recorder, s => s.RecordAsync(company, request, Key()))).Payment.Id;
        await Pay(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
        return payment;
    }

    private async Task<Guid> RecordedAsync(Guid company, long amount, RecordCreditPaymentRequest? request = null) =>
        (await Pay(await UserAsync(), s => s.RecordAsync(company, request ?? new RecordCreditPaymentRequest("rtgs", amount, Today, Ref()), Key()))).Payment.Id;

    private async Task<int> CountAsync(string sql)
    {
        await using var db = fixture.CreateContext();
        return (await db.Database.SqlQueryRaw<int>(sql).ToListAsync()).Single();
    }

    // ---- aging -------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Aging_PutsEveryOrder_InExactlyOneBucket_AtTheExactEdges()
    {
        var name = "Edges " + Guid.NewGuid().ToString("N")[..8];
        var company = await CompanyAsync(name);
        // One order per edge; the amount names the edge (days + 1000) so a wrong bucket shows which one moved.
        foreach (var days in new[] { -1, 0, 1, 30, 31, 60, 61, 90, 91 }) await OrderDueAsync(company, 1_000 + days, days);

        var row = (await Read(s => s.AgingAsync(company, null, false, 1, 10))).Companies.Items.Single();
        row.Buckets.Should().Be(new CreditAgingBuckets(
            Current: 999 + 1_000, Days1To30: 1_001 + 1_030, Days31To60: 1_031 + 1_060, Days61To90: 1_061 + 1_090, Days90Plus: 1_091));
        (row.OpenOrders, row.ReceivableMinor, row.OverdueMinor, row.MaxDaysPastDue).Should().Be((9, row.Buckets.Total, row.Buckets.Overdue, 91));
        row.OldestDueOn.Should().Be(Today.AddDays(-91));
    }

    [PostgresFact]
    public async Task Aging_CountsTheReceivable_NotTheCharge_AndDropsAPaidOffOrder()
    {
        var company = await CompanyAsync("Partial " + Guid.NewGuid().ToString("N")[..8]);
        await OrderDueAsync(company, 50_000, 100);
        await OrderDueAsync(company, 20_000, 5);
        await OrderDueAsync(company, 7_000, 40);
        await PaidAsync(company, 60_000);

        var row = (await Read(s => s.AgingAsync(company, null, false, 1, 10))).Companies.Items.Single();
        // The planner paid the 100-day order (50,000) and the 40-day order (7,000 in full), then 3,000 of the 5-day order.
        row.Buckets.Should().Be(new CreditAgingBuckets(0, 17_000, 0, 0, 0));
        (row.OpenOrders, row.OnAccountMinor).Should().Be((1, 0));

        await PaidAsync(company, 17_000);
        (await Read(s => s.AgingAsync(company, null, false, 1, 10))).Companies.Items.Should().BeEmpty("nothing is owed any more");
    }

    [PostgresFact]
    public async Task Aging_Filters_Totals_AndPaging()
    {
        var tag = Guid.NewGuid().ToString("N")[..8];
        var (a, b, c) = (await CompanyAsync($"Aging {tag} A"), await CompanyAsync($"Aging {tag} B"), await CompanyAsync($"Aging {tag} C"));
        await OrderDueAsync(a, 3_000, 45);
        await OrderDueAsync(b, 5_000, 2);
        await OrderDueAsync(b, 1_000, 0);
        await OrderDueAsync(c, 8_000, -9);   // not due yet

        var all = await Read(s => s.AgingAsync(null, tag, false, 1, 2));
        all.Companies.TotalCount.Should().Be(3);
        all.Companies.Items.Select(r => r.CompanyId).Should().Equal([b, a], "the most overdue first, two on the page");
        all.Totals.Should().Be(new CreditAgingBuckets(Current: 9_000, Days1To30: 5_000, Days31To60: 3_000, Days61To90: 0, Days90Plus: 0));
        (all.ReceivableMinor, all.OverdueMinor).Should().Be((17_000, 8_000), "the totals cover every match, not just the page");
        (await Read(s => s.AgingAsync(null, tag, false, 2, 2))).Companies.Items.Select(r => r.CompanyId).Should().Equal(c);

        (await Read(s => s.AgingAsync(null, tag, true, 1, 10))).Companies.Items.Select(r => r.CompanyId).Should().Equal(b, a);
        (await Read(s => s.AgingAsync(b, null, false, 1, 10))).Companies.Items.Select(r => r.CompanyId).Should().Equal(b);
        (await Read(s => s.AgingAsync(null, tag.ToUpperInvariant() + " c", false, 1, 10))).Companies.Items.Select(r => r.CompanyId).Should().Equal(c);
        (await Read(s => s.AgingAsync(Guid.NewGuid(), null, false, 1, 10))).Companies.TotalCount.Should().Be(0);
        (await Read(s => s.AgingAsync(null, tag, false, 0, 10_000))).Companies.Should().Match<CreditPage<CreditAgingRow>>(p => p.Page == 1 && p.PageSize == CreditReadService.MaxPageSize);
    }

    [PostgresFact]
    public async Task Aging_ShowsTheHold_AsOfNow_AndALiftEndsIt()
    {
        var company = await CompanyAsync("Hold " + Guid.NewGuid().ToString("N")[..8]);
        await OrderDueAsync(company, 4_000, 15);   // exactly 15 days past due: not held
        (await Read(s => s.AgingAsync(company, null, false, 1, 5))).Companies.Items.Single().OnHold.Should().BeFalse();
        await OrderDueAsync(company, 4_000, 16);
        (await Read(s => s.AgingAsync(company, null, false, 1, 5))).Companies.Items.Single().OnHold.Should().BeTrue();
        (await Read(s => s.AgingAsync(company, null, false, 1, 5), options: new CreditOptions { HoldAfterDaysPastDue = 0 })).Companies.Items.Single().OnHold
            .Should().BeFalse("flag only: nothing is held");

        await using (var db = fixture.CreateContext())
            await db.Companies.Where(c => c.Id == company).ExecuteUpdateAsync(s => s.SetProperty(c => c.CreditHoldOverrideUntilUtc, DateTime.UtcNow.AddDays(2)));
        var lifted = (await Read(s => s.AgingAsync(company, null, false, 1, 5))).Companies.Items.Single();
        (lifted.OnHold, lifted.HoldOverrideUntilUtc is not null).Should().Be((false, true));
    }

    // ---- statement ---------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Statement_ListsTheLines_WithOrderNumbersAndPaymentReferences_AndTheCurrentTotalsOfTheSameSnapshot()
    {
        var company = await CompanyAsync("Statement " + Guid.NewGuid().ToString("N")[..8]);
        var order = await OrderDueAsync(company, 30_000, 3);
        var payment = await PaidAsync(company, 45_000);
        var number = await OrderNumberAsync(order);

        var statement = await Read(s => s.StatementAsync(company, null, null, 1, 50));
        var lines = statement.Lines.Items;
        lines.Select(l => (l.Seq, l.Kind)).Should().Equal((1L, "charge"), (2L, "payment_applied"), (3L, "payment_on_account"));
        lines[0].OrderNumber.Should().Be(number);
        lines[1].Should().Match<CreditLedgerLineDto>(l => l.PaymentId == payment && l.PaymentMethod == "rtgs" && l.PaymentReference!.StartsWith("TRX") && l.ReceivableDeltaMinor == -30_000);
        (lines[^1].ReceivableAfterMinor, lines[^1].OnAccountAfterMinor).Should().Be((statement.CurrentReceivableMinor, statement.CurrentOnAccountMinor));
        (statement.CurrentReceivableMinor, statement.CurrentOnAccountMinor, statement.Lines.TotalCount).Should().Be((0, 15_000, 3));

        var firstPage = await Read(s => s.StatementAsync(company, null, null, 1, 2));
        (firstPage.Lines.Items.Count, firstPage.Lines.TotalCount, firstPage.Lines.Items[^1].Seq).Should().Be((2, 3, 2L));
        (await Read(s => s.StatementAsync(company, null, null, 2, 2))).Lines.Items.Select(l => l.Seq).Should().Equal(3L);
        (await Read(s => s.StatementAsync(company, Today.AddDays(1), null, 1, 50))).Lines.TotalCount.Should().Be(0);
        (await Read(s => s.StatementAsync(company, Today, Today, 1, 50))).Lines.Items.Select(l => l.Seq).Should().Equal(2L, 3L);
        (await Read(s => s.StatementAsync(company, null, Today.AddDays(-1), 1, 50))).Lines.Items.Select(l => l.Seq).Should().Equal(1L);   // the charge is about the day it was confirmed, 33 days ago
    }

    [PostgresFact]
    public async Task Statement_IsRefusedFor_UnknownAndPendingCompanies_AndABackwardsRange()
    {
        var pending = await PendingCompanyAsync();
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Read(s => s.StatementAsync(pending, null, null, 1, 10)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Read(s => s.StatementAsync(Guid.NewGuid(), null, null, 1, 10)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Read(s => s.StatementCsvAsync(Guid.NewGuid(), null, null)));
        var company = await CompanyAsync("Range " + Guid.NewGuid().ToString("N")[..8]);
        await Assert.ThrowsAsync<ValidationException>(() => Read(s => s.StatementAsync(company, Today, Today.AddDays(-1), 1, 10)));
    }

    [PostgresFact]
    public async Task ASuspendedCompany_StillHasAStatementAndAnAgingRow()
    {
        var company = await CompanyAsync("Suspended " + Guid.NewGuid().ToString("N")[..8]);
        await OrderDueAsync(company, 9_000, 3);
        await using (var db = fixture.CreateContext()) await new CompanyService(db).AdminSuspendAsync(company);
        (await Read(s => s.StatementAsync(company, null, null, 1, 10))).Lines.TotalCount.Should().Be(1);
        (await Read(s => s.AgingAsync(company, null, false, 1, 10))).Companies.TotalCount.Should().Be(1);
    }

    /// <summary>A statement is one moment: while payments are verified and charges land, every read's lines still add up to the totals beside them.</summary>
    [PostgresFact]
    public async Task EveryReadDuringParallelWrites_IsInternallyConsistent()
    {
        var company = await CompanyAsync("Parallel " + Guid.NewGuid().ToString("N")[..8]);
        for (var i = 0; i < 12; i++) await OrderDueAsync(company, 10_000, i);
        var writers = Enumerable.Range(0, 12).Select(_ => Task.Run(() => PaidAsync(company, 7_500))).ToList();
        var readers = Enumerable.Range(0, 40).Select(_ => Task.Run(async () =>
        {
            var statement = await Read(s => s.StatementAsync(company, null, null, 1, CreditReadService.MaxPageSize));
            var lines = statement.Lines.Items;
            lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, lines.Count).Select(i => (long)i), "no gap in the chain");
            lines.Sum(l => l.ReceivableDeltaMinor).Should().Be(statement.CurrentReceivableMinor);
            lines.Sum(l => l.OnAccountDeltaMinor).Should().Be(statement.CurrentOnAccountMinor);
            var row = (await Read(s => s.AgingAsync(company, null, false, 1, 5))).Companies.Items.SingleOrDefault();
            (row?.ReceivableMinor ?? 0).Should().BeLessThanOrEqualTo(120_000);
        })).ToList();
        await Task.WhenAll(writers.Cast<Task>().Concat(readers));
    }

    [PostgresFact]
    public async Task Reads_NeverWaitForARowLock_AndWriteNoLedgerLine()
    {
        var company = await CompanyAsync("Locks " + Guid.NewGuid().ToString("N")[..8]);
        await OrderDueAsync(company, 20_000, 4);
        var payment = await RecordedAsync(company, 5_000);
        var firstOrder = await FirstOrderAsync(company);
        var adjustment = (await Adj(await UserAsync(), s => s.RequestAsync(company, new RequestCreditAdjustmentRequest("write_off", 1_000, "customer disputes the freight", OrderId: firstOrder), Key()))).Adjustment.Id;
        var linesBefore = await CountAsync("SELECT count(*)::int AS \"Value\" FROM credit_ledger_entries");

        // A writer holds the company, the payment, the adjustment and the order rows the way a verification, a cancel and an approval do.
        await using var holder = Ctx();
        await using var tx = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM credit_payments WHERE id = {payment} FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM credit_adjustments WHERE id = {adjustment} FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id IN (SELECT order_id FROM credit_ledger_entries WHERE company_id = {company}) FOR UPDATE");
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {company} FOR UPDATE");

        var reads = Task.WhenAll(
            Read(s => s.StatementAsync(company, null, null, 1, 10)), Read(s => s.AgingAsync(company, null, false, 1, 10)),
            Read(s => s.GetPaymentAsync(payment)), Read(s => s.GetAdjustmentAsync(adjustment)),
            Read(s => s.ListPaymentsAsync("all", company, 1, 10)), Read(s => s.ListAdjustmentsAsync("all", company, 1, 10)));
        (await Task.WhenAny(reads, Task.Delay(TimeSpan.FromSeconds(20)))).Should().BeSameAs(reads, "a read never queues behind a writer's row lock");
        await reads;
        await tx.RollbackAsync();
        (await CountAsync("SELECT count(*)::int AS \"Value\" FROM credit_ledger_entries")).Should().Be(linesBefore);
    }

    // ---- queues ------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ThePaymentQueue_ShowsRecordedOldestFirst_WithItsFlags_AndFiltersByStatusAndCompany()
    {
        var company = await CompanyAsync("Queue " + Guid.NewGuid().ToString("N")[..8]);
        var other = await CompanyAsync("Queue other " + Guid.NewGuid().ToString("N")[..8]);
        var first = await RecordedAsync(company, 1_000);
        var late = await RecordedAsync(company, 2_000, new RecordCreditPaymentRequest("rtgs", 2_000, Today.AddDays(-45), Ref(), LateEntryReason: "the slip came with the courier"));
        var postDated = await RecordedAsync(company, 3_000, new RecordCreditPaymentRequest("cheque", 3_000, Today, "CHQ" + Ref(), "Sonali Bank", ChequeDate: Today.AddDays(10)));
        var verified = await PaidAsync(company, 4_000);
        var elsewhere = await RecordedAsync(other, 5_000);

        var queue = await Read(s => s.ListPaymentsAsync(null, company, 1, 10));
        queue.Items.Select(i => i.Payment.Id).Should().Equal([first, late, postDated], "recorded is the default and oldest comes first");
        queue.Items.Select(i => (i.LateEntry, i.PostDated)).Should().Equal((false, false), (true, false), (false, true));
        queue.Items[0].CompanyName.Should().StartWith("Queue ");
        queue.TotalCount.Should().Be(3);

        (await Read(s => s.ListPaymentsAsync("verified", company, 1, 10))).Items.Select(i => i.Payment.Id).Should().Equal(verified);
        (await Read(s => s.ListPaymentsAsync("ALL", company, 1, 10))).TotalCount.Should().Be(4);
        (await Read(s => s.ListPaymentsAsync("recorded", other, 1, 10))).Items.Select(i => i.Payment.Id).Should().Equal(elsewhere);
        (await Read(s => s.ListPaymentsAsync(null, company, 2, 2))).Items.Select(i => i.Payment.Id).Should().Equal(postDated);
        foreach (var bad in new[] { "pending", "1", "recorded;drop", "" + (int)CreditPaymentStatus.Bounced })
            await Assert.ThrowsAsync<ValidationException>(() => Read(s => s.ListPaymentsAsync(bad, company, 1, 10)));
    }

    [PostgresFact]
    public async Task OnePayment_ShowsItsLines_AndTheStaffNote_AndUnknownIsNotFound()
    {
        var company = await CompanyAsync("OnePayment " + Guid.NewGuid().ToString("N")[..8]);
        var order = await OrderDueAsync(company, 6_000, 2);
        var payment = await PaidAsync(company, 10_000, note: "slip is in the green folder");

        var detail = await Read(s => s.GetPaymentAsync(payment));
        (detail.Payment.Id, detail.Payment.Status, detail.Payment.Note, detail.CompanyName).Should().Be((payment, "verified", "slip is in the green folder", detail.CompanyName));
        detail.Lines.Select(l => l.Kind).Should().Equal("payment_applied", "payment_on_account");
        detail.Lines[0].OrderNumber.Should().Be(await OrderNumberAsync(order));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Read(s => s.GetPaymentAsync(Guid.NewGuid())));
    }

    [PostgresFact]
    public async Task TheAdjustmentQueue_AndOneAdjustment_ShowTheRequestThenTheApprovedLine()
    {
        var company = await CompanyAsync("Adjust " + Guid.NewGuid().ToString("N")[..8]);
        var order = await OrderDueAsync(company, 9_000, 40);
        var (requester, approver) = (await UserAsync(), await UserAsync());
        var asked = (await Adj(requester, s => s.RequestAsync(company, new RequestCreditAdjustmentRequest("write_off", 2_500, "=cmd|' /C calc'!A0 bad debt", OrderId: order), Key()))).Adjustment.Id;

        (await Read(s => s.ListAdjustmentsAsync(null, company, 1, 10))).Items.Select(i => i.Adjustment.Id).Should().Equal(asked);
        (await Read(s => s.ListAdjustmentsAsync("approved", company, 1, 10))).TotalCount.Should().Be(0);
        (await Read(s => s.GetAdjustmentAsync(asked))).Lines.Should().BeEmpty("a request moves no money");

        await Adj(approver, s => s.ApproveAsync(asked));
        (await Read(s => s.ListAdjustmentsAsync(null, company, 1, 10))).TotalCount.Should().Be(0);
        var approved = await Read(s => s.GetAdjustmentAsync(asked));
        approved.Adjustment.Status.Should().Be("approved");
        approved.Lines.Single().Should().Match<CreditLedgerLineDto>(l => l.Kind == "write_off" && l.ReceivableDeltaMinor == -2_500 && l.AdjustmentId == asked);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => Read(s => s.GetAdjustmentAsync(Guid.NewGuid())));
        await Assert.ThrowsAsync<ValidationException>(() => Read(s => s.ListAdjustmentsAsync("done", company, 1, 10)));
    }

    // ---- CSV (CS27) --------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task CreditExport_IsAudited_AndNeutralisesFormulas()
    {
        var company = await CompanyAsync("=HYPERLINK(\"http://evil.test\")" + Guid.NewGuid().ToString("N")[..6]);
        var order = await OrderDueAsync(company, 9_000, 40);
        var (requester, approver, reader) = (await UserAsync(), await UserAsync(), await UserAsync());
        var asked = (await Adj(requester, s => s.RequestAsync(company, new RequestCreditAdjustmentRequest("write_off", 2_500, "=cmd|' /C calc'!A0 bad debt", OrderId: order), Key()))).Adjustment.Id;
        await Adj(approver, s => s.ApproveAsync(asked));

        var statement = await Read(s => s.StatementCsvAsync(company, null, null), reader);
        statement.Content.Should().StartWith("﻿\"seq\",\"occurred_on\"");
        statement.Rows.Should().Be(2);
        statement.Content.Should().Contain("\"'=cmd|' /C calc'!A0 bad debt\"", "a reason that starts with = gets a leading '");
        statement.Content.Should().Contain("\"-2500\",\"-25.00\"", "a negative amount stays a number");
        var rows = statement.Content.TrimStart('﻿').Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        rows.Should().OnlyContain(r => r.StartsWith('"') && r.EndsWith('"'), "every cell is quoted");
        rows[0].Split("\",\"").Length.Should().Be(rows[1].Split("\",\"").Length);

        var aging = await Read(s => s.AgingCsvAsync(false), reader);
        aging.Content.Should().Contain("\"'=HYPERLINK(").And.NotContain(",\"=HYPERLINK(");
        aging.Rows.Should().BeGreaterThanOrEqualTo(1);

        await using var db = fixture.CreateContext();
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.ActorId == reader && a.Action.StartsWith("credit.") && a.Action.EndsWith("_exported")).OrderBy(a => a.CreatedAtUtc).ToListAsync();
        audit.Select(a => a.Action).Should().Equal("credit.statement_exported", "credit.aging_exported");
        audit[0].EntityId.Should().Be(company.ToString());
        audit[0].NewValuesJson.Should().Contain("\"rows\": 2");
        (audit[0].ActorIp, audit[0].CorrelationId).Should().Be((Ip, "corr-read"));
        (await db.AuditLogs.CountAsync(a => a.ActorId == reader)).Should().Be(2, "JSON reads write no audit row; only exports do");
    }

    // ---- helpers -----------------------------------------------------------------------------------------------------------

    private async Task<string> OrderNumberAsync(Guid order)
    {
        await using var db = fixture.CreateContext();
        return await db.Orders.AsNoTracking().Where(o => o.Id == order).Select(o => o.OrderNumber).SingleAsync();
    }

    private async Task<Guid> FirstOrderAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == company && l.OrderId != null).OrderBy(l => l.Seq).Select(l => l.OrderId!.Value).FirstAsync();
    }

    private async Task<Guid> PendingCompanyAsync()
    {
        var owner = await UserAsync();
        await using var db = fixture.CreateContext();
        return (await new CompanyService(db).RegisterAsync(owner, new RegisterCompanyRequest("Pending Reads Ltd", "TL-B38-P",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
    }
}
