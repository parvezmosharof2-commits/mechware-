using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// B3.6, threat model §১২ CS20, CS21 (and CS16, CS29 for adjustments) against a real database with every ledger guard installed:
/// a request moves nothing; only an approval by someone else writes ONE new line, under the locks credit_adjustments → the source
/// credit_payments row → companies, with its audit row and outbox event; every cap is checked again under those locks.
/// </summary>
public class CreditAdjustmentServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Ip = "203.0.113.36";
    private static long _binSeq = 3_950_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);
    private static readonly CreditOptions NoWait = new() { PayoutAfterClearingDays = 0 };
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

    private MechwareDbContext Ctx() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private static Staff Who(Guid actor, string? corr) => new(actor, corr ?? "corr-" + Guid.NewGuid().ToString("N")[..8]);

    private async Task<T> Pay<T>(Guid actor, Func<CreditPaymentService, Task<T>> action)
    {
        await using var db = Ctx();
        return await action(new CreditPaymentService(db, Who(actor, null), new CreditOptions(), TimeProvider.System));
    }

    private async Task<T> Adj<T>(Guid actor, Func<CreditAdjustmentService, Task<T>> action, CreditOptions? options = null, string? corr = null)
    {
        await using var db = Ctx();
        return await action(new CreditAdjustmentService(db, Who(actor, corr), options ?? new CreditOptions(), TimeProvider.System));
    }

    private sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers);

    private async Task<Team> TeamAsync(long limitMinor = 10_000_000, int buyers = 0, bool activate = true)
    {
        var admin = await UserAsync();
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await UserAsync());
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Adjusted Traders Ltd", "TL-B36-1",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        foreach (var buyer in buyerIds)
            await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        if (!activate) return new Team(company, admin, buyerIds);
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, limitMinor));
        return new Team(company, admin, buyerIds);
    }

    private async Task<Guid> ChargedOrderAsync(Team t, long amount, int daysAgo = 5)
    {
        await using var db = fixture.CreateContext();
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, amount);
        var company = await db.Companies.SingleAsync(c => c.Id == t.Company);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == t.Company).ToListAsync();
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        db.CreditLedgerEntries.Add(new CreditLedger(company, lines).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return order;
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    private sealed record Paid(Guid Id, Guid Recorder, Guid Verifier);

    /// <summary>Recorded by one person, verified (cleared today) by another: the planner pays open orders, the rest goes on account.</summary>
    private async Task<Paid> PaidAsync(Team t, long amount, string method = "rtgs")
    {
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var request = method switch
        {
            "cheque" => new RecordCreditPaymentRequest(method, amount, Today, "CHQ" + Ref(), "Sonali Bank", ChequeDate: Today),
            "cash" => new RecordCreditPaymentRequest(method, amount, Today),
            _ => new RecordCreditPaymentRequest(method, amount, Today, Ref())
        };
        var payment = (await Pay(recorder, s => s.RecordAsync(t.Company, request, Key()))).Payment.Id;
        await Pay(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
        return new Paid(payment, recorder, verifier);
    }

    private static RequestCreditAdjustmentRequest WriteOff(Guid order, long amount) =>
        new("write_off", amount, "customer closed; bad debt agreed", OrderId: order);

    private static RequestCreditAdjustmentRequest Payout(Guid payment, long amount) =>
        new("on_account_payout", amount, "over-payment returned on request", PaymentId: payment, PayoutReference: "MW-OUT-" + Guid.NewGuid().ToString("N")[..8]);

    private static RequestCreditAdjustmentRequest Correction(Guid payment, long amount) =>
        new("on_account_correction", amount, "deposit slip was mis-keyed", PaymentId: payment);

    private Task<RequestedCreditAdjustment> AskAsync(Guid actor, Guid company, RequestCreditAdjustmentRequest r, string? key = null, CreditOptions? options = null) =>
        Adj(actor, s => s.RequestAsync(company, r, key ?? Key()), options);

    private async Task<Guid> AskedAsync(Guid actor, Guid company, RequestCreditAdjustmentRequest r, CreditOptions? options = null) =>
        (await AskAsync(actor, company, r, options: options)).Adjustment.Id;

    private Task<ApprovedCreditAdjustment> ApproveAsync(Guid actor, Guid adjustment, CreditOptions? options = null, string? corr = null) =>
        Adj(actor, s => s.ApproveAsync(adjustment), options, corr);

    private Task<BouncedCreditPayment> BounceAsync(Guid actor, Guid payment) =>
        Pay(actor, s => s.BounceAsync(payment, new BounceCreditPaymentRequest("cheque returned: account closed")));

    private async Task<CreditReductionResult?> ReduceAsync(Guid order)
    {
        await using var db = Ctx();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {order} FOR NO KEY UPDATE");
        var result = await new CreditLedgerService(db, Who(Guid.NewGuid(), "corr-cancel"), TimeProvider.System)
            .ReduceChargeAsync(new CreditReduction(order, CreditReductionKind.OrderCancelled, null, $"cancel:{order:N}", null, null, "order cancelled"));
        await tx.CommitAsync();
        return result;
    }

    private async Task<(Company Company, List<CreditLedgerEntry> Lines)> StateAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company),
            await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == company).OrderBy(l => l.Seq).ToListAsync());
    }

    private async Task<CreditAdjustment> AdjustmentAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.CreditAdjustments.AsNoTracking().SingleAsync(a => a.Id == id);
    }

    /// <summary>I1, I2 (also per source), I4/I6 and the chain over every line of the company, from the database.</summary>
    private async Task<List<CreditLedgerEntry>> LedgerAddsUpAsync(Guid companyId)
    {
        var (company, lines) = await StateAsync(companyId);
        lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, lines.Count).Select(i => (long)i));
        company.CreditUsedMinor.Should().Be(lines.Sum(l => l.ReceivableDeltaMinor));
        company.CreditOnAccountMinor.Should().Be(lines.Sum(l => l.OnAccountDeltaMinor));
        CreditOnAccount.Sources(lines).Sum(s => s.OnAccountMinor).Should().Be(company.CreditOnAccountMinor, "no source below zero");
        var ledger = new CreditLedger(company, lines);
        foreach (var order in lines.Where(l => l.OrderId is not null).Select(l => l.OrderId!.Value).Distinct())
        {
            var b = ledger.BalanceOf(order);
            (b.ReceivableMinor >= 0 && b.PaidMinor >= 0 && b.ReducedMinor <= b.ChargeMinor).Should().BeTrue($"order {order}: {b}");
        }
        lines.Where(l => l.AdjustmentId is not null).Should().OnlyContain(l => l.ReceivableDeltaMinor <= 0, "an adjustment never raises a debt");
        return lines;
    }

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ConflictException>(act)).Code;

    // ---- CS20 ----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AWriteOff_IsPostedOnlyByTheApproval_AsOneNewLine_WithItsAuditRowAndOutboxEvent()
    {
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 50_000);
        var (finance, admin) = (await UserAsync(), await UserAsync());

        var asked = await AskAsync(finance, t.Company, WriteOff(order, 30_000));
        asked.Should().BeEquivalentTo(new { Replayed = false, Adjustment = new { Kind = "write_off", Status = "requested", RequestedBy = finance, AmountMinor = 30_000L } });
        var before = await StateAsync(t.Company);
        before.Company.CreditUsedMinor.Should().Be(50_000, "a request moves nothing");

        var approved = await ApproveAsync(admin, asked.Adjustment.Id, corr: "corr-approve");

        approved.Should().BeEquivalentTo(new
        {
            Adjustment = new { Status = "approved", ApprovedBy = (Guid?)admin },
            ReceivableDeltaMinor = -30_000L,
            OnAccountDeltaMinor = 0L,
            CreditUsedMinor = 20_000L,
            CreditOnAccountMinor = 0L
        });
        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Take(before.Lines.Count).Should().BeEquivalentTo(before.Lines, "an approval never edits an old line");
        lines.Skip(before.Lines.Count).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = CreditLedgerKind.WriteOff,
            OrderId = (Guid?)order,
            AdjustmentId = (Guid?)asked.Adjustment.Id,
            ActorUserId = (Guid?)admin,
            CorrelationId = "corr-approve",
            EventId = approved.EventId,
            OccurredOn = Today,
            IdempotencyKey = $"adjust:{asked.Adjustment.Id:N}",
            Reason = "customer closed; bad debt agreed"
        });

        await using var db = fixture.CreateContext();
        var audits = await db.AuditLogs.AsNoTracking().Where(a => a.EntityId == asked.Adjustment.Id.ToString()).OrderBy(a => a.CreatedAtUtc).ToListAsync();
        audits.Select(a => (a.Action, a.ActorId)).Should().Equal(("credit.adjustment_requested", (Guid?)finance), ("credit.adjustment_approved", admin));
        audits[1].Should().BeEquivalentTo(new { ActorIp = Ip, CorrelationId = "corr-approve", EntityName = "credit_adjustments" });
        using (var old = JsonDocument.Parse(audits[1].OldValuesJson!))
            old.RootElement.GetProperty("creditUsedMinor").GetInt64().Should().Be(50_000);
        using (var after = JsonDocument.Parse(audits[1].NewValuesJson!))
            (after.RootElement.GetProperty("eventId").GetGuid(), after.RootElement.GetProperty("creditUsedMinor").GetInt64()).Should().Be((approved.EventId, 20_000));
        (await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == approved.EventId)).Should()
            .BeEquivalentTo(new { EventType = "credit.adjustment_approved", AggregateId = asked.Adjustment.Id });
    }

    [PostgresFact]
    public async Task WriteOff_AboveTheOutstanding_IsRefused_AtTheRequest_AndAgainAtTheApproval()
    {
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 50_000);
        var (finance, admin) = (await UserAsync(), await UserAsync());
        (await CodeOf(() => AskAsync(finance, t.Company, WriteOff(order, 50_001)))).Should().Be("CREDIT_EXCEEDS_RECEIVABLE");

        // Asked while 50,000 was owed; a payment of 30,000 came in before the approval: the approval checks again, never clamps.
        var asked = await AskedAsync(finance, t.Company, WriteOff(order, 40_000));
        await PaidAsync(t, 30_000);
        (await CodeOf(() => ApproveAsync(admin, asked))).Should().Be("CREDIT_EXCEEDS_RECEIVABLE");
        (await AdjustmentAsync(asked)).Status.Should().Be(CreditAdjustmentStatus.Requested, "a refused approval changes nothing");
        (await StateAsync(t.Company)).Lines.Should().NotContain(l => l.Kind == CreditLedgerKind.WriteOff);
        await Assert.ThrowsAsync<ValidationException>(() => AskAsync(finance, t.Company, WriteOff(order, -5_000)));   // CS20: never a debt
    }

    [PostgresFact]
    public async Task Adjustment_ApprovedByItsRequester_IsRefused()
    {
        // Nor by whoever recorded or verified the payment whose money it moves (CS20); the database CHECK backs the first rule.
        var t = await TeamAsync();
        var paid = await PaidAsync(t, 10_000, "cash");
        var admin = await UserAsync();
        var asked = await AskedAsync(admin, t.Company, Correction(paid.Id, -1_000));

        foreach (var who in new[] { admin, paid.Recorder, paid.Verifier })
            (await CodeOf(() => ApproveAsync(who, asked))).Should().Be("CREDIT_ADJUSTMENT_FOUR_EYES");
        (await AdjustmentAsync(asked)).Status.Should().Be(CreditAdjustmentStatus.Requested);
        (await ApproveAsync(await UserAsync(), asked)).OnAccountDeltaMinor.Should().Be(-1_000);
        await LedgerAddsUpAsync(t.Company);
    }

    // ---- CS21 ----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Payout_AboveTheOnAccountBalance_IsRefused()
    {
        var t = await TeamAsync();
        var paid = await PaidAsync(t, 60_000, "cash");     // all on account (nothing open)
        var (finance, admin) = (await UserAsync(), await UserAsync());
        (await CodeOf(() => AskAsync(finance, t.Company, Payout(paid.Id, 60_001)))).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");

        // Asked for 50,000; 20,000 of the money was applied to a new order before the approval: only 40,000 is left.
        var asked = await AskedAsync(finance, t.Company, Payout(paid.Id, 50_000));
        await ChargedOrderAsync(t, 20_000);
        await Pay(await UserAsync(), s => s.ApplyOnAccountAsync(t.Company, new ApplyOnAccountRequest(), Key()));
        (await CodeOf(() => ApproveAsync(admin, asked))).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");

        var exact = await AskedAsync(finance, t.Company, Payout(paid.Id, 40_000));
        (await ApproveAsync(admin, exact)).Should().BeEquivalentTo(new { OnAccountDeltaMinor = -40_000L, CreditOnAccountMinor = 0L, CreditUsedMinor = 0L });
        var lines = await LedgerAddsUpAsync(t.Company);
        lines[^1].Should().BeEquivalentTo(new { Kind = CreditLedgerKind.OnAccountPayout, PaymentId = (Guid?)paid.Id, AdjustmentId = (Guid?)exact });
    }

    [PostgresFact]
    public async Task APayout_OfMoneyThatCanStillBounce_WaitsTheClearingPeriod_ByDefault()
    {
        var t = await TeamAsync();
        var transfer = await PaidAsync(t, 30_000);
        var finance = await UserAsync();
        (await CodeOf(() => AskAsync(finance, t.Company, Payout(transfer.Id, 1_000)))).Should().Be("CREDIT_PAYOUT_TOO_EARLY", "cleared today, 30 days by default");

        // Asked while the setting allowed it; the approval reads the setting again.
        var asked = await AskedAsync(finance, t.Company, Payout(transfer.Id, 1_000), NoWait);
        var admin = await UserAsync();
        (await CodeOf(() => ApproveAsync(admin, asked))).Should().Be("CREDIT_PAYOUT_TOO_EARLY");
        (await ApproveAsync(admin, asked, NoWait)).OnAccountDeltaMinor.Should().Be(-1_000);

        // A bounced payment's money cannot be paid out or corrected at all.
        await BounceAsync(await UserAsync(), transfer.Id);
        (await CodeOf(() => AskAsync(finance, t.Company, Payout(transfer.Id, 1), options: NoWait))).Should().Be("CREDIT_ADJUSTMENT_SOURCE_NOT_VERIFIED");
        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Where(l => l.PaymentId == transfer.Id).Sum(l => l.OnAccountDeltaMinor).Should().Be(0, "the bounce took back only what was left: 29,000");
    }

    // ---- request: idempotency, 404, targets -------------------------------------------------------------------------------

    [PostgresFact]
    public async Task RequestAdjustment_ReplayWithTheSameKey_ReturnsTheSameAdjustment_AndAnotherBodyIs409()
    {
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 50_000);
        var finance = await UserAsync();
        const string key = "adjust-replay-0001";

        using var gate = new ManualResetEventSlim(false);
        var racers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () => { gate.Wait(); return await AskAsync(finance, t.Company, WriteOff(order, 5_000), key); })).ToList();
        gate.Set();
        var outcomes = await Task.WhenAll(racers);

        outcomes.Count(o => !o.Replayed).Should().Be(1);
        outcomes.Select(o => o.Adjustment.Id).Distinct().Should().ContainSingle();
        (await CodeOf(() => AskAsync(finance, t.Company, WriteOff(order, 5_001), key))).Should().Be("IDEMPOTENCY_KEY_REUSED");
        var other = (await TeamAsync()).Company;
        (await CodeOf(() => AskAsync(finance, other, WriteOff(order, 5_000), key))).Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await AskAsync(await UserAsync(), t.Company, WriteOff(order, 5_000), key)).Replayed.Should().BeFalse("another person's key is their own");
        foreach (var bad in new[] { null, "", "short", new string('k', 101), "has space here" })
            await Assert.ThrowsAsync<ValidationException>(() => Adj(finance, s => s.RequestAsync(t.Company, WriteOff(order, 1), bad)));
        await using var db = fixture.CreateContext();
        (await db.CreditAdjustments.CountAsync(a => a.CompanyId == t.Company)).Should().Be(2);
        (await db.AuditLogs.CountAsync(a => a.Action == "credit.adjustment_requested" && a.EntityId == outcomes[0].Adjustment.Id.ToString())).Should().Be(1);
    }

    [PostgresFact]
    public async Task RequestAdjustment_ForAnUnknownOrPendingCompany_Is404_AndAnotherCompanysOrderOrPaymentIs409()
    {
        var finance = await UserAsync();
        foreach (var company in new[] { (await TeamAsync(activate: false)).Company, Guid.NewGuid() })
            await Assert.ThrowsAsync<KeyNotFoundException>(() => AskAsync(finance, company, WriteOff(Guid.NewGuid(), 1)));

        var (mine, theirs) = (await TeamAsync(), await TeamAsync());
        var theirPayment = await PaidAsync(theirs, 10_000, "cash");    // nothing open yet: all on account
        var theirOrder = await ChargedOrderAsync(theirs, 10_000);
        (await CodeOf(() => AskAsync(finance, mine.Company, WriteOff(theirOrder, 1)))).Should().Be("CREDIT_ADJUSTMENT_TARGET_NOT_FOUND");
        (await CodeOf(() => AskAsync(finance, mine.Company, WriteOff(Guid.NewGuid(), 1)))).Should().Be("CREDIT_ADJUSTMENT_TARGET_NOT_FOUND");
        (await CodeOf(() => AskAsync(finance, mine.Company, Correction(theirPayment.Id, -1)))).Should().Be("CREDIT_ADJUSTMENT_TARGET_NOT_FOUND");
        (await CodeOf(() => AskAsync(finance, mine.Company, Correction(Guid.NewGuid(), -1)))).Should().Be("CREDIT_ADJUSTMENT_TARGET_NOT_FOUND");
        foreach (var bad in new RequestCreditAdjustmentRequest[]
                 {
                     new("forgive", 1, "not a kind we know", OrderId: theirOrder), new("write_off", 1, "too short", OrderId: theirOrder),
                     new("write_off", 1, "dollars are not taken", OrderId: theirOrder, Currency: "USD")
                 })
            await Assert.ThrowsAsync<ValidationException>(() => AskAsync(finance, mine.Company, bad));

        // A suspended company is still adjusted (it still owes and still holds its money).
        await using (var db = fixture.CreateContext()) await new CompanyService(db).AdminSuspendAsync(theirs.Company);
        (await AskAsync(finance, theirs.Company, WriteOff(theirOrder, 1))).Adjustment.Status.Should().Be("requested");
    }

    // ---- decisions ------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task RejectOrWithdraw_MovesNothing_AndADecidedAdjustmentCannotBeDecidedAgain()
    {
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 50_000);
        var (finance, admin) = (await UserAsync(), await UserAsync());
        var withdrawn = await AskedAsync(finance, t.Company, WriteOff(order, 1_000));
        var rejected = await AskedAsync(finance, t.Company, WriteOff(order, 2_000));
        var before = await StateAsync(t.Company);

        (await Adj(finance, s => s.RejectAsync(withdrawn, new RejectCreditAdjustmentRequest("asked by mistake")))).Should()
            .BeEquivalentTo(new { Status = "rejected", RejectedBy = (Guid?)finance, DecisionReason = "asked by mistake" });
        await Assert.ThrowsAsync<ValidationException>(() => Adj(admin, s => s.RejectAsync(rejected, new RejectCreditAdjustmentRequest("no"))));
        await Adj(admin, s => s.RejectAsync(rejected, new RejectCreditAdjustmentRequest("not agreed by the board")));
        foreach (var id in new[] { withdrawn, rejected })
        {
            (await CodeOf(() => ApproveAsync(admin, id))).Should().Be("CREDIT_ADJUSTMENT_NOT_REQUESTED");
            (await CodeOf(() => Adj(admin, s => s.RejectAsync(id, new RejectCreditAdjustmentRequest("again, by mistake"))))).Should().Be("CREDIT_ADJUSTMENT_NOT_REQUESTED");
        }
        await Assert.ThrowsAsync<KeyNotFoundException>(() => ApproveAsync(admin, Guid.NewGuid()));
        (await StateAsync(t.Company)).Lines.Should().HaveCount(before.Lines.Count, "a rejection moves no money");
        await using var db = fixture.CreateContext();
        (await db.AuditLogs.CountAsync(a => a.Action == "credit.adjustment_rejected" && (a.EntityId == withdrawn.ToString() || a.EntityId == rejected.ToString()))).Should().Be(2);
    }

    [PostgresFact]
    public async Task TwoParallelApprovals_PostOnce()
    {
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 50_000);
        var asked = await AskedAsync(await UserAsync(), t.Company, WriteOff(order, 10_000));
        var admins = new[] { await UserAsync(), await UserAsync(), await UserAsync() };

        using var gate = new ManualResetEventSlim(false);
        var racers = admins.Select(a => Task.Run(async () =>
        {
            gate.Wait();
            try { await ApproveAsync(a, asked); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })).ToList();
        gate.Set();
        (await Task.WhenAll(racers)).Should().BeEquivalentTo(["ok", "CREDIT_ADJUSTMENT_NOT_REQUESTED", "CREDIT_ADJUSTMENT_NOT_REQUESTED"]);
        (await LedgerAddsUpAsync(t.Company)).Count(l => l.AdjustmentId == asked).Should().Be(1);
    }

    // ---- CS16: the lock order ---------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AWriteOffApproval_DoesNotWaitForACancelHoldingItsOrder_ButAPayoutWaitsForABounceHoldingItsPayment()
    {
        // A cancel holds its order NO KEY UPDATE before it queues for the company: the write-off line names that order (FOR KEY SHARE
        // through its foreign key) and must not wait for it. A payout locks its source payment (adjustment → payment → company, like a
        // bounce: payment → company), so a bounce holding that payment makes the payout wait, then the payout sees what the bounce did.
        var t = await TeamAsync();
        var paid = await PaidAsync(t, 20_000);                       // nothing open yet: all on account
        var order = await ChargedOrderAsync(t, 30_000);
        var finance = await UserAsync();
        var writeOff = await AskedAsync(finance, t.Company, WriteOff(order, 10_000));
        var payout = await AskedAsync(finance, t.Company, Payout(paid.Id, 5_000), NoWait);

        await using (var cancel = Ctx())
        {
            await using var tx = await cancel.Database.BeginTransactionAsync();
            await cancel.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {order} FOR NO KEY UPDATE");
            var approval = Task.Run(async () => await ApproveAsync(await UserAsync(), writeOff));
            (await Task.WhenAny(approval, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(approval, "a write-off does not wait for a cancel");
            (await approval).CreditUsedMinor.Should().Be(20_000);
            await tx.RollbackAsync();
        }

        await using (var bounce = Ctx())
        {
            await using var tx = await bounce.Database.BeginTransactionAsync();
            await bounce.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM credit_payments WHERE id = {paid.Id} FOR NO KEY UPDATE");
            var approval = Task.Run(async () => await ApproveAsync(await UserAsync(), payout, NoWait));
            (await Task.WhenAny(approval, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(approval, "the payout waits for the payment's lock");
            await tx.RollbackAsync();
            (await approval).OnAccountDeltaMinor.Should().Be(-5_000);
        }

        // An approval holding the payment and queued for the company must not block a writer that holds the company and writes a
        // line naming that payment (a cancel's on-account line, an apply): that line's foreign key takes FOR KEY SHARE on the payment.
        var held = await AskedAsync(finance, t.Company, Payout(paid.Id, 1_000), NoWait);
        await using (var writer = Ctx())
        {
            await using var tx = await writer.Database.BeginTransactionAsync();
            await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {t.Company} FOR UPDATE");
            var approval = Task.Run(async () => await ApproveAsync(await UserAsync(), held, NoWait));
            (await Task.WhenAny(approval, Task.Delay(TimeSpan.FromSeconds(2)))).Should().NotBeSameAs(approval, "it waits for the company");
            var keyShare = writer.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM credit_payments WHERE id = {paid.Id} FOR KEY SHARE");
            (await Task.WhenAny(keyShare, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(keyShare, "FOR UPDATE on the payment would deadlock here");
            await keyShare;
            await tx.RollbackAsync();
            (await approval).OnAccountDeltaMinor.Should().Be(-1_000);
        }

        // Once the bounce has happened, a payout asked before it finds the payment bounced and pays out nothing.
        var late = await AskedAsync(finance, t.Company, Payout(paid.Id, 5_000), NoWait);
        await BounceAsync(await UserAsync(), paid.Id);
        var approver = await UserAsync();
        (await CodeOf(() => ApproveAsync(approver, late, NoWait))).Should().Be("CREDIT_ADJUSTMENT_SOURCE_NOT_VERIFIED");
        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Where(l => l.PaymentId == paid.Id).Sum(l => l.OnAccountDeltaMinor).Should().Be(0);
        lines[^1].OnAccountDeltaMinor.Should().Be(-14_000, "the bounce takes back what was still on account, not the 6,000 paid out");
    }

    [PostgresFact]
    public async Task AdjustVerifyBounceCancelAndApply_InParallel_NeverDeadlock_AndTheLedgerAddsUp()
    {
        // One company, all at once: 6 write-off approvals (adjustment → company; their orders are also cancelled in parallel), 4 payouts
        // and 4 negative corrections (adjustment → payment → company; two of their payments also bounce in parallel), 6 verifications
        // and 2 bounces (payment → company), 6 cancels (order → company), 4 applies (company) and 4 new credit orders (price lists →
        // company). No deadlock (40P01), no lost update; an approval may only find the money or the debt gone (409); afterwards I1,
        // I2 per source, I4/I6 hold and no adjustment raised a debt.
        const long Unit = 10_000;
        var t = await TeamAsync(limitMinor: 1_000 * Unit, buyers: 4);
        var variants = new List<Guid>();
        await using (var db = fixture.CreateContext())
            for (var i = 0; i < 4; i++) variants.Add((await CatalogTestData.SeedVariantAsync(db, 5, priceMinor: Unit)).VariantId);
        await PaidAsync(t, 10 * Unit, "cash");   // the oldest on-account money: the applies spend it first, it never bounces
        var sources = new List<Paid>();
        for (var i = 0; i < 4; i++) sources.Add(await PaidAsync(t, 10 * Unit, i % 2 == 0 ? "cheque" : "rtgs"));   // on account
        var orders = new List<Guid>();
        for (var i = 0; i < 6; i++) orders.Add(await ChargedOrderAsync(t, 3 * Unit, daysAgo: 25 - i));
        await ChargedOrderAsync(t, 40 * Unit, daysAgo: 1);
        var (finance, recorder, verifier, bouncer) = (await UserAsync(), await UserAsync(), await UserAsync(), await UserAsync());
        var approvals = new List<Guid>();
        foreach (var o in orders) approvals.Add(await AskedAsync(finance, t.Company, WriteOff(o, 2 * Unit)));
        foreach (var s in sources)
        {
            approvals.Add(await AskedAsync(finance, t.Company, Payout(s.Id, 3 * Unit), NoWait));
            approvals.Add(await AskedAsync(finance, t.Company, Correction(s.Id, -2 * Unit), NoWait));
        }
        var toVerify = new List<Guid>();
        for (var i = 0; i < 6; i++)
            toVerify.Add((await Pay(recorder, s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest("npsb", 2 * Unit, Today, Ref()), Key()))).Payment.Id);
        var admins = new List<Guid>();
        for (var i = 0; i < approvals.Count; i++) admins.Add(await UserAsync());

        using var gate = new ManualResetEventSlim(false);
        var refusals = new System.Collections.Concurrent.ConcurrentBag<string>();
        async Task Refusable(Func<Task> act)
        {
            gate.Wait();
            try { await act(); }
            catch (ConflictException ex) when (ex.Code is "CREDIT_EXCEEDS_RECEIVABLE" or "CREDIT_ON_ACCOUNT_INSUFFICIENT"
                or "CREDIT_ADJUSTMENT_SOURCE_NOT_VERIFIED" or "CREDIT_NOTHING_TO_APPLY")
            { refusals.Add(ex.Code); }
        }
        var work = new List<Task>();
        work.AddRange(approvals.Select((a, i) => Task.Run(() => Refusable(() => ApproveAsync(admins[i], a, NoWait)))));
        work.AddRange(orders.Select(o => Task.Run(async () => { gate.Wait(); await ReduceAsync(o); })));
        work.AddRange(toVerify.Select(p => Task.Run(async () => { gate.Wait(); await Pay(verifier, s => s.VerifyAsync(p, new VerifyCreditPaymentRequest(Today))); })));
        work.AddRange(sources.Take(2).Select(p => Task.Run(async () => { gate.Wait(); await BounceAsync(bouncer, p.Id); })));
        work.AddRange(Enumerable.Range(0, 4).Select(_ => Task.Run(() => Refusable(async () =>
            await Pay(await UserAsync(), s => s.ApplyOnAccountAsync(t.Company, new ApplyOnAccountRequest(2 * Unit), Key()))))));
        work.AddRange(t.Buyers.Select((b, i) => Task.Run(async () => { gate.Wait(); await PlaceAsync(b, t.Company, variants[i]); })));
        gate.Set();
        var all = Task.WhenAll(work);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120)))).Should().BeSameAs(all, "a deadlock would hang or abort");
        await all;   // any 40P01, lost update or broken chain throws here

        var lines = await LedgerAddsUpAsync(t.Company);
        // The applies (8 units) spend the older cash first and the bounces hit sources 0 and 1 only; sources 2 and 3 always hold their
        // 10 units, so their payout (3) and correction (−2) are always approved, whatever order the work ran in.
        var kept = sources.Skip(2).Select(s => (Guid?)s.Id).ToList();
        lines.Count(l => l.AdjustmentId is not null && kept.Contains(l.PaymentId)).Should().Be(4, string.Join(",", refusals));
        foreach (var o in orders)
            lines.Where(l => l.OrderId == o).Sum(l => l.ReceivableDeltaMinor).Should().Be(0, "a cancelled order owes nothing, whatever ran before");
        foreach (var s in sources.Take(2))
            lines.Where(l => l.PaymentId == s.Id).Sum(l => l.OnAccountDeltaMinor).Should().Be(0, "a bounced payment holds nothing");
        await using (var db = fixture.CreateContext())
        {
            var decided = await db.CreditAdjustments.AsNoTracking().Where(a => approvals.Contains(a.Id)).ToListAsync();
            decided.Where(a => a.Status == CreditAdjustmentStatus.Approved).Select(a => a.Id).Should()
                .BeEquivalentTo(lines.Where(l => l.AdjustmentId is not null).Select(l => l.AdjustmentId!.Value), "approved ⇔ exactly one line");
            (await db.CreditPayments.CountAsync(p => toVerify.Contains(p.Id) && p.Status == CreditPaymentStatus.Verified)).Should().Be(6);
        }
    }

    private async Task<PlacedOrder> PlaceAsync(Guid buyer, Guid company, Guid variant)
    {
        await using var db = fixture.CreateContext();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db));
        return await orders.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(variant, 1)], null, company, PayOnCredit: true));
    }
}
