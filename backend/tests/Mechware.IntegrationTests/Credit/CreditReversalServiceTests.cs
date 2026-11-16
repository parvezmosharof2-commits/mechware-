using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
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
/// B3.5, threat model §১২ CS12 (bounce), CS19 (charge reduction, service level: E7.2 and E6.5 call it later), CS16 (the settlement
/// half of the lock order) and CS29 for both, against a real database with every ledger guard installed: old lines are never
/// changed, a bounce may take credit used above the limit while a charge never can (the guard that replaced the CHECK).
/// </summary>
public class CreditReversalServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Ip = "203.0.113.9";
    private static long _binSeq = 3_800_000_000;
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

    private MechwareDbContext Ctx() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private async Task<T> As<T>(Guid actor, Func<CreditPaymentService, Task<T>> action, string? correlationId = null)
    {
        await using var db = Ctx();
        return await action(new CreditPaymentService(db, new Staff(actor, correlationId ?? "corr-" + Guid.NewGuid().ToString("N")[..8]), new CreditOptions(), TimeProvider.System));
    }

    private sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers);

    private async Task<Team> TeamAsync(long limitMinor = 10_000_000, int buyers = 0)
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await TestUsers.CreateAsync(fixture));
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Bounced Mills Ltd", "TL-B35-1",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        foreach (var buyer in buyerIds)
            await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, limitMinor));
        return new Team(company, admin, buyerIds);
    }

    private async Task<Guid> ChargedOrderAsync(Team t, long amount, int daysAgo = 5)
    {
        await using var db = fixture.CreateContext();
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, amount);
        var company = await db.Companies.SingleAsync(c => c.Id == t.Company);
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return order;
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    /// <summary>Recorded by one person, verified by another; returns the payment.</summary>
    private async Task<Guid> PaidAsync(Team t, long amount, IReadOnlyList<CreditAllocationInput>? allocations = null, string method = "cheque")
    {
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var request = method == "cheque"
            ? new RecordCreditPaymentRequest(method, amount, Today, "CHQ" + Ref(), "Sonali Bank", ChequeDate: Today, Allocations: allocations)
            : new RecordCreditPaymentRequest(method, amount, Today, method == "cash" ? null : Ref(), Allocations: allocations);
        var payment = (await As(recorder, s => s.RecordAsync(t.Company, request, Key()))).Payment.Id;
        await As(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
        return payment;
    }

    private Task<BouncedCreditPayment> BounceAsync(Guid actor, Guid payment, string reason = "cheque returned: insufficient funds", string? corr = null) =>
        As(actor, s => s.BounceAsync(payment, new BounceCreditPaymentRequest(reason)), corr);

    /// <summary>What E7.2 will do: lock the order row (NO KEY UPDATE, the contract), reduce inside the same transaction, commit (or roll back).</summary>
    private async Task<CreditReductionResult?> ReduceAsync(Guid order, long? amount, string sourceKey, Guid? creditNote = null, bool commit = true,
        Guid? actor = null)
    {
        await using var db = Ctx();
        await using var tx = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {order} FOR NO KEY UPDATE");
        var ledger = new CreditLedgerService(db, new Staff(actor ?? Guid.NewGuid(), "corr-reduce"), TimeProvider.System);
        var result = await ledger.ReduceChargeAsync(new CreditReduction(order, creditNote is null ? CreditReductionKind.OrderCancelled : CreditReductionKind.CreditNote,
            amount, sourceKey, creditNote, actor, "order cancelled by the buyer"));
        if (commit) await tx.CommitAsync();
        return result;
    }

    private async Task<(Company Company, List<CreditLedgerEntry> Lines)> StateAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company),
            await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == company).OrderBy(l => l.Seq).ToListAsync());
    }

    /// <summary>I1-I4 and the chain over every line of the company, from the database.</summary>
    private async Task<List<CreditLedgerEntry>> LedgerAddsUpAsync(Guid companyId)
    {
        var (company, lines) = await StateAsync(companyId);
        lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, lines.Count).Select(i => (long)i));
        company.CreditUsedMinor.Should().Be(lines.Sum(l => l.ReceivableDeltaMinor));
        company.CreditOnAccountMinor.Should().Be(lines.Sum(l => l.OnAccountDeltaMinor));
        var ledger = new CreditLedger(company, lines);
        foreach (var order in lines.Where(l => l.OrderId is not null).Select(l => l.OrderId!.Value).Distinct())
        {
            var b = ledger.BalanceOf(order);
            (b.ReceivableMinor >= 0 && b.PaidMinor >= 0 && b.ReducedMinor <= b.ChargeMinor).Should().BeTrue($"order {order}: {b}");
        }
        return lines;
    }

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ConflictException>(act)).Code;

    // ---- CS12 bounce -------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task ABouncedCheque_PutsTheDebtBackOnTheSameOrders()
    {
        // A cheque of 100,000 paid two orders and left 10,000 on account. The bounce (by the verifier: no four eyes) writes new
        // payment_reversed lines, the old lines stay as they were, the due dates stay, so the overdue order is overdue again.
        var t = await TeamAsync();
        var overdue = await ChargedOrderAsync(t, 60_000, daysAgo: 40);
        var recent = await ChargedOrderAsync(t, 30_000, daysAgo: 2);
        var newest = await ChargedOrderAsync(t, 50_000, daysAgo: 1);
        var cheque = await PaidAsync(t, 100_000, [new(overdue, 60_000), new(recent, 30_000)]);   // the last 10,000 to the newest by FIFO
        var before = await StateAsync(t.Company);
        before.Company.CreditUsedMinor.Should().Be(40_000);
        var bouncer = await UserAsync();

        var result = await BounceAsync(bouncer, cheque, corr: "corr-bounce");

        result.Reversed.Should().Equal(new CreditAllocation(overdue, 60_000), new CreditAllocation(recent, 30_000), new CreditAllocation(newest, 10_000));
        (result.OnAccountReversedMinor, result.CreditUsedMinor, result.CreditOnAccountMinor).Should().Be((0, 140_000, 0));
        result.Payment.Should().BeEquivalentTo(new { Status = "bounced", BouncedBy = (Guid?)bouncer, DecisionReason = "cheque returned: insufficient funds" });

        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Take(before.Lines.Count).Should().BeEquivalentTo(before.Lines, "a bounce never edits an old line");
        var bounce = lines.Skip(before.Lines.Count).ToList();
        bounce.Should().HaveCount(3).And.OnlyContain(l => l.Kind == CreditLedgerKind.PaymentReversed && l.PaymentId == cheque && l.ActorUserId == bouncer
            && l.CorrelationId == "corr-bounce" && l.EventId == result.EventId && l.OccurredOn == Today && l.DueOn == null);
        lines.Single(l => l.OrderId == overdue && l.Kind == CreditLedgerKind.Charge).DueOn.Should().Be(Today.AddDays(-40 + 30), "the due date stays");

        await using var db = fixture.CreateContext();
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "credit.payment_bounced" && a.EntityId == cheque.ToString());
        audit.Should().BeEquivalentTo(new { ActorId = (Guid?)bouncer, ActorIp = Ip, CorrelationId = "corr-bounce", EntityName = "credit_payments" });
        using (var after = JsonDocument.Parse(audit.NewValuesJson!))
            (after.RootElement.GetProperty("eventId").GetGuid(), after.RootElement.GetProperty("creditUsedMinor").GetInt64()).Should().Be((result.EventId, 140_000));
        (await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.Id == result.EventId)).EventType.Should().Be("credit.payment_bounced");
        (await db.CreditPayments.AsNoTracking().SingleAsync(p => p.Id == cheque)).Status.Should().Be(CreditPaymentStatus.Bounced);
    }

    [PostgresFact]
    public async Task ABounce_AfterOnAccountWasApplied_ReversesThatApplicationToo()
    {
        // 30,000 on account from a transfer; 20,000 of it applied to a later order (the apply-on-account line is posted directly
        // here: its API is B3.5b). The bounce puts 20,000 back on that order and takes the last 10,000 off the account.
        var t = await TeamAsync();
        var transfer = await PaidAsync(t, 30_000, method: "beftn");
        var later = await ChargedOrderAsync(t, 50_000, daysAgo: 0);
        await using (var db = fixture.CreateContext())
        {
            var company = await db.Companies.SingleAsync(c => c.Id == t.Company);
            var ledger = new CreditLedger(company, await db.CreditLedgerEntries.Where(l => l.CompanyId == t.Company).ToListAsync());
            db.CreditLedgerEntries.Add(ledger.Post(new CreditPosting(CreditLedgerKind.OnAccountApplied, -20_000, -20_000, $"apply:test:{later:N}", Today, later, transfer),
                DateTime.UtcNow).Entry);
            await db.SaveChangesAsync();
        }
        (await StateAsync(t.Company)).Company.Should().BeEquivalentTo(new { CreditUsedMinor = 30_000L, CreditOnAccountMinor = 10_000L });

        var result = await BounceAsync(await UserAsync(), transfer);

        result.Reversed.Should().Equal(new CreditAllocation(later, 20_000));
        (result.OnAccountReversedMinor, result.CreditUsedMinor, result.CreditOnAccountMinor).Should().Be((10_000, 50_000, 0));
        await LedgerAddsUpAsync(t.Company);
    }

    [PostgresFact]
    public async Task BouncingTwice_IsAppliedOnce()
    {
        // Four people press "bounce" at once: the payment row lock lets one through; the others find it bounced (409). A recorded
        // payment cannot bounce (nothing was released) and cash never does.
        var t = await TeamAsync();
        await ChargedOrderAsync(t, 80_000);
        var cheque = await PaidAsync(t, 50_000);
        var people = new List<Guid>();
        for (var i = 0; i < 4; i++) people.Add(await UserAsync());

        using var gate = new ManualResetEventSlim(false);
        var racers = people.Select(p => Task.Run(async () =>
        {
            gate.Wait();
            try { await BounceAsync(p, cheque); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })).ToList();
        gate.Set();
        var outcomes = await Task.WhenAll(racers);

        outcomes.Count(o => o == "ok").Should().Be(1, string.Join(",", outcomes));
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "CREDIT_PAYMENT_NOT_VERIFIED");
        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Count(l => l.Kind == CreditLedgerKind.PaymentReversed).Should().Be(1);
        (await StateAsync(t.Company)).Company.CreditUsedMinor.Should().Be(80_000);

        var recorder = await UserAsync();
        var recorded = (await As(recorder, s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest("rtgs", 1_000, Today, Ref()), Key()))).Payment.Id;
        (await CodeOf(() => BounceAsync(people[0], recorded))).Should().Be("CREDIT_PAYMENT_NOT_VERIFIED");
        var (cash, cheque2) = (await PaidAsync(t, 1_000, method: "cash"), await PaidAsync(t, 1_000));
        (await CodeOf(() => BounceAsync(people[0], cash))).Should().Be("CREDIT_PAYMENT_CASH_CANNOT_BOUNCE");
        await Assert.ThrowsAsync<ValidationException>(() => BounceAsync(people[0], cheque2, reason: "no"));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => BounceAsync(people[0], Guid.NewGuid()));
        (await StateAsync(t.Company)).Lines.Count(l => l.Kind == CreditLedgerKind.PaymentReversed).Should().Be(1);
    }

    [PostgresFact]
    public async Task ABounce_MayTakeCreditUsedAboveTheLimit_ButAChargeNeverCan()
    {
        // ADR-0010 §1: the limit is 100,000 and fully used again after a payment freed it; the bounce puts the debt back (200,000
        // used). The guard that replaced ck_companies_credit_within_limit still refuses, in the database, a charge above the
        // limit and a limit lowered below the credit used.
        var t = await TeamAsync(limitMinor: 100_000);
        await ChargedOrderAsync(t, 100_000);
        var cheque = await PaidAsync(t, 100_000);
        await ChargedOrderAsync(t, 100_000);

        (await BounceAsync(await UserAsync(), cheque)).CreditUsedMinor.Should().Be(200_000);
        var (company, lines) = await StateAsync(t.Company);
        (company.CreditUsedMinor, company.CreditLimitMinor).Should().Be((200_000, 100_000));
        company.CreditAvailable.ToMinorUnits().Should().Be(-100_000, "the UIs show it as 0 (B3.9)");
        (await CodeOf(() => ChargedOrderAsync(t, 1))).Should().Be("CREDIT_LIMIT_EXCEEDED");

        await using var db = fixture.CreateContext();
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, 1);
        var charge = await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO credit_ledger_entries (id, company_id, seq, event_id, kind, order_id, receivable_delta_minor, on_account_delta_minor,
                receivable_after_minor, on_account_after_minor, due_on, occurred_on, currency, idempotency_key, created_at)
            VALUES ({Guid.NewGuid()}, {t.Company}, {lines.Count + 1}, {Guid.NewGuid()}, 'charge', {order}, 1, 0, 200001, 0, {Today}, {Today}, 'BDT',
                {"charge:" + order.ToString("N")}, now())
            """));
        (charge.SqlState, charge.MessageText).Should().Be((PostgresErrorCodes.CheckViolation,
            $"credit_ledger_entries: a charge would take the credit used of company {t.Company} to 200001, above its limit 100000"));
        var lower = await Assert.ThrowsAsync<PostgresException>(() =>
            db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET credit_limit = 50000 WHERE id = {t.Company}"));
        lower.SqlState.Should().Be(PostgresErrorCodes.CheckViolation);
        (await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE companies SET credit_limit = 150000 WHERE id = {t.Company}"))
            .Should().Be(1, "raising the limit is allowed, even while it stays below the credit used");
    }

    // ---- CS19 charge reduction (service level) -----------------------------------------------------------------------

    [PostgresFact]
    public async Task CancellingAConfirmedCreditOrder_ReleasesItsCredit_ThroughAReductionEntry()
    {
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 70_000);
        var other = await ChargedOrderAsync(t, 10_000);
        var actor = await UserAsync();
        var before = await StateAsync(t.Company);

        // Rolled back with its caller: nothing is left behind.
        (await ReduceAsync(order, null, $"cancel:{order:N}", commit: false))!.ReceivableReleasedMinor.Should().Be(70_000);
        (await StateAsync(t.Company)).Lines.Should().HaveCount(before.Lines.Count);

        var result = await ReduceAsync(order, null, $"cancel:{order:N}", actor: actor);

        result.Should().Be(new CreditReductionResult(70_000, 0, false));
        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Take(before.Lines.Count).Should().BeEquivalentTo(before.Lines);
        lines.Skip(before.Lines.Count).Should().ContainSingle().Which.Should().BeEquivalentTo(new
        {
            Kind = CreditLedgerKind.ChargeReduced,
            OrderId = (Guid?)order,
            ReceivableDeltaMinor = -70_000L,
            OnAccountDeltaMinor = 0L,
            ActorUserId = (Guid?)actor,
            CorrelationId = "corr-reduce",
            IdempotencyKey = $"reduce:cancel:{order:N}:receivable",
            Reason = "order cancelled by the buyer"
        });
        (await StateAsync(t.Company)).Company.CreditUsedMinor.Should().Be(10_000);
        await using (var db = fixture.CreateContext())
        {
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "credit.charge_reduced" && a.EntityId == order.ToString());
            audit.Should().BeEquivalentTo(new { ActorId = (Guid?)actor, ActorIp = Ip, CorrelationId = "corr-reduce", EntityName = "orders" });
        }

        // Never charged (not on credit, or cancelled while waiting for approval): nothing to do. No transaction: refused.
        await using (var db = fixture.CreateContext())
        {
            var notCharged = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, 5_000, status: "PendingApproval");
            (await ReduceAsync(notCharged, null, $"cancel:{notCharged:N}")).Should().BeNull();
            await Assert.ThrowsAsync<InvalidOperationException>(() => new CreditLedgerService(db, new Staff(actor, "c"), TimeProvider.System)
                .ReduceChargeAsync(new CreditReduction(other, CreditReductionKind.OrderCancelled, null, $"cancel:{other:N}", null, actor, "x")));
        }
        await using (var db = Ctx())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await Assert.ThrowsAsync<ValidationException>(() => new CreditLedgerService(db, new Staff(actor, "c"), TimeProvider.System)
                .ReduceChargeAsync(new CreditReduction(other, CreditReductionKind.CreditNote, 1_000, "creditnote:no-id", null, actor, "credit note without its id")));
        }
        (await StateAsync(t.Company)).Company.CreditUsedMinor.Should().Be(10_000);
    }

    [PostgresFact]
    public async Task ThePaidPart_OfACancelledOrder_GoesOnAccount()
    {
        // Charge 100,000, credit note 20,000, paid 30,000, so 50,000 owed. Cancelling: 50,000 released, 30,000 on account in the
        // payment's name, which does not raise available credit until someone applies it.
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 100_000);
        (await ReduceAsync(order, 20_000, "creditnote:cn-1", Guid.NewGuid()))!.ReceivableReleasedMinor.Should().Be(20_000);
        var transfer = await PaidAsync(t, 30_000, method: "rtgs");

        var result = await ReduceAsync(order, null, $"cancel:{order:N}");

        result.Should().Be(new CreditReductionResult(50_000, 30_000, false));
        var lines = await LedgerAddsUpAsync(t.Company);
        lines[^1].Should().BeEquivalentTo(new { Kind = CreditLedgerKind.ChargeReduced, OnAccountDeltaMinor = 30_000L, PaymentId = (Guid?)transfer, OrderId = (Guid?)order });
        (await StateAsync(t.Company)).Company.Should().BeEquivalentTo(new { CreditUsedMinor = 0L, CreditOnAccountMinor = 30_000L });

        // The transfer bounces afterwards: the money it brought leaves the account again; the cancelled order owes nothing.
        (await BounceAsync(await UserAsync(), transfer)).Should().BeEquivalentTo(new { OnAccountReversedMinor = 30_000L, CreditUsedMinor = 0L, CreditOnAccountMinor = 0L });
        await LedgerAddsUpAsync(t.Company);
    }

    [PostgresFact]
    public async Task ReducingACharge_TwiceWithTheSameKey_AppliesOnce()
    {
        // The same cancellation four times at once (a retried cancel), and a credit note replayed with another amount.
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 40_000);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(() => ReduceAsync(order, null, $"cancel:{order:N}"))));

        outcomes.Count(r => r!.AlreadyApplied).Should().Be(3);
        outcomes.Should().OnlyContain(r => r!.ReceivableReleasedMinor == 40_000);
        (await LedgerAddsUpAsync(t.Company)).Count(l => l.Kind == CreditLedgerKind.ChargeReduced).Should().Be(1);

        var noted = await ChargedOrderAsync(t, 40_000);
        var note = Guid.NewGuid();
        (await ReduceAsync(noted, 15_000, $"creditnote:{note:N}", note))!.AlreadyApplied.Should().BeFalse();
        (await ReduceAsync(noted, 15_000, $"creditnote:{note:N}", note))!.AlreadyApplied.Should().BeTrue();
        (await CodeOf(() => ReduceAsync(noted, 16_000, $"creditnote:{note:N}", note))).Should().Be("CREDIT_REDUCTION_CONFLICT");
        (await StateAsync(t.Company)).Company.CreditUsedMinor.Should().Be(25_000);
    }

    [PostgresFact]
    public async Task CreditNoteAndCancellation_TogetherNeverExceedTheCharge()
    {
        // A credit note and the cancellation of the same order race; whichever comes second gets only what is left (or 409).
        var t = await TeamAsync();
        for (var round = 0; round < 5; round++)
        {
            var order = await ChargedOrderAsync(t, 50_000);
            var note = Guid.NewGuid();
            var both = await Task.WhenAll(
                Task.Run(async () => { try { return (await ReduceAsync(order, 30_000, $"creditnote:{note:N}", note))!.ReceivableReleasedMinor; } catch (ConflictException) { return 0L; } }),
                Task.Run(async () => (await ReduceAsync(order, null, $"cancel:{order:N}"))!.ReceivableReleasedMinor));

            both.Sum().Should().Be(50_000, $"round {round}: {both[0]} + {both[1]}");
            var lines = await LedgerAddsUpAsync(t.Company);
            lines.Where(l => l.OrderId == order && l.Kind == CreditLedgerKind.ChargeReduced).Sum(l => -l.ReceivableDeltaMinor).Should().Be(50_000);
        }
        var first = (await StateAsync(t.Company)).Lines[0].OrderId!.Value;
        (await CodeOf(() => ReduceAsync(first, 1, "creditnote:late", Guid.NewGuid()))).Should().Be("CREDIT_REDUCTION_EXCEEDS_CHARGE");
    }

    // ---- CS16, the settlement half: cancels, verifications, bounces and new orders at once ---------------------------

    private async Task WaitUntilSomeoneWaitsForALockAsync()
    {
        await using var db = Ctx();
        for (var i = 0; i < 200; i++)
        {
            var waiting = await db.Database.SqlQuery<int>(
                $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE datname = current_database() AND wait_event_type = 'Lock'").SingleAsync();
            if (waiting > 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Nobody queued for the lock.");
    }

    [PostgresFact]
    public async Task ACancel_AndABounceOrVerificationOfTheSameMoney_AtOnce_DoNotDeadlock()
    {
        // The two cycles foreign keys could close (every ledger line takes FOR KEY SHARE on the order and payment it names):
        // 1. the cancel holds the order and the company; a bounce of the payment that paid the order holds the payment and waits
        //    for the company; the cancel's on-account line names that payment. FOR UPDATE on the payment would deadlock here.
        var t = await TeamAsync();
        var order = await ChargedOrderAsync(t, 50_000);
        var cheque = await PaidAsync(t, 20_000);
        var bouncer = await UserAsync();
        CreditReductionResult? reduced;
        Task<BouncedCreditPayment> bounce;
        await using (var db = Ctx())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {order} FOR NO KEY UPDATE");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {t.Company} FOR UPDATE");
            bounce = Task.Run(() => BounceAsync(bouncer, cheque));
            await WaitUntilSomeoneWaitsForALockAsync();
            reduced = await new CreditLedgerService(db, new Staff(bouncer, "c"), TimeProvider.System)
                .ReduceChargeAsync(new CreditReduction(order, CreditReductionKind.OrderCancelled, null, $"cancel:{order:N}", null, null, "cancelled"));
            await tx.CommitAsync();
        }
        reduced.Should().Be(new CreditReductionResult(30_000, 20_000, false));
        (await bounce).Should().BeEquivalentTo(new { Reversed = Array.Empty<CreditAllocation>(), OnAccountReversedMinor = 20_000L, CreditUsedMinor = 0L, CreditOnAccountMinor = 0L });

        // 2. the cancel holds the order (NO KEY UPDATE, the ICreditLedger contract); a verification takes the company and writes a
        //    line naming that order. It must finish while the cancel still holds the order; with FOR UPDATE it would wait for the
        //    cancel, which then waits for the company.
        var second = await ChargedOrderAsync(t, 40_000);
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var payment = (await As(recorder, s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest("npsb", 10_000, Today, Ref()), Key()))).Payment.Id;
        await using (var db = Ctx())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {second} FOR NO KEY UPDATE");
            var verify = Task.Run(() => As(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today))));
            (await Task.WhenAny(verify, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(verify, "the verification does not wait for the cancel");
            (await verify).Applied.Should().Equal(new CreditAllocation(second, 10_000));
            reduced = await new CreditLedgerService(db, new Staff(verifier, "c"), TimeProvider.System)
                .ReduceChargeAsync(new CreditReduction(second, CreditReductionKind.OrderCancelled, null, $"cancel:{second:N}", null, null, "cancelled"));
            await tx.CommitAsync();
        }
        reduced.Should().Be(new CreditReductionResult(30_000, 10_000, false));
        await LedgerAddsUpAsync(t.Company);
    }

    private async Task<PlacedOrder> PlaceAsync(Guid buyer, Guid company, Guid variant)
    {
        await using var db = fixture.CreateContext();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db));
        return await orders.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(variant, 1)], null, company, PayOnCredit: true));
    }

    [PostgresFact]
    public async Task ReduceVerifyBounceAndPlace_InParallel_NeverDeadlock_AndTheLedgerAddsUp()
    {
        // 8 cancels (order row → company row), 6 verifications and 4 bounces (payment row → company row) and 8 new credit orders
        // (price lists → company row) of one company, all at once. One lock order means no deadlock (40P01) and no lost update:
        // every cancel, verification and bounce succeeds, and the ledger chain ends at the company's cached totals.
        const long Unit = 10_000;
        var t = await TeamAsync(limitMinor: 1_000 * Unit, buyers: 8);
        var variants = new List<Guid>();
        await using (var db = fixture.CreateContext())
            for (var i = 0; i < 8; i++) variants.Add((await CatalogTestData.SeedVariantAsync(db, 5, priceMinor: Unit)).VariantId);
        var cancels = new List<Guid>();
        for (var i = 0; i < 8; i++) cancels.Add(await ChargedOrderAsync(t, 3 * Unit, daysAgo: 20 - i));
        await ChargedOrderAsync(t, 50 * Unit, daysAgo: 1);
        var toBounce = new List<Guid>();
        for (var i = 0; i < 4; i++) toBounce.Add(await PaidAsync(t, 2 * Unit));   // FIFO: they pay the oldest orders, which are cancelled below
        var (recorder, verifier, bouncer) = (await UserAsync(), await UserAsync(), await UserAsync());
        var toVerify = new List<Guid>();
        for (var i = 0; i < 6; i++)
            toVerify.Add((await As(recorder, s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest("rtgs", 2 * Unit, Today, Ref()), Key()))).Payment.Id);

        using var gate = new ManualResetEventSlim(false);
        var work = new List<Task>();
        work.AddRange(cancels.Select(o => Task.Run(async () => { gate.Wait(); await ReduceAsync(o, null, $"cancel:{o:N}"); })));
        work.AddRange(toVerify.Select(p => Task.Run(async () => { gate.Wait(); await As(verifier, s => s.VerifyAsync(p, new VerifyCreditPaymentRequest(Today))); })));
        work.AddRange(toBounce.Select(p => Task.Run(async () => { gate.Wait(); await BounceAsync(bouncer, p); })));
        work.AddRange(t.Buyers.Select((b, i) => Task.Run(async () => { gate.Wait(); await PlaceAsync(b, t.Company, variants[i]); })));
        gate.Set();
        var all = Task.WhenAll(work);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(90)))).Should().BeSameAs(all, "a deadlock would hang or abort");
        await all;   // any 40P01, lost update or broken chain throws here

        var lines = await LedgerAddsUpAsync(t.Company);
        lines.Count(l => l.Kind == CreditLedgerKind.PaymentReversed).Should().BeGreaterThanOrEqualTo(4);
        foreach (var o in cancels)
            lines.Where(l => l.OrderId == o).Sum(l => l.ReceivableDeltaMinor).Should().Be(0, "a cancelled order owes nothing, whatever ran before");
        await using (var db = fixture.CreateContext())
            (await db.CreditPayments.CountAsync(p => toBounce.Contains(p.Id) && p.Status == CreditPaymentStatus.Bounced)).Should().Be(4);
    }
}
