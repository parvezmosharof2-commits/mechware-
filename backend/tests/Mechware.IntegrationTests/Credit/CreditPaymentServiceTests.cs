using System.Data.Common;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
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
/// B3.4, threat model §১২ CS1, CS2, CS6-CS8, CS11, CS15, CS17, CS24, CS29, CS31 at service level: the verification transaction of
/// ADR-0010 §7 (payment row lock → company row lock → planner → ledger lines → audit row → outbox event, one COMMIT) against a
/// real database, its four-eyes rule, its races and its crash safety.
/// </summary>
public class CreditPaymentServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Ip = "203.0.113.7";
    private static long _binSeq = 3_600_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
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

    private async Task<T> As<T>(Guid actor, Func<CreditPaymentService, Task<T>> action, CreditOptions? options = null, string? correlationId = null,
        IInterceptor? extra = null)
    {
        var builder = new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(fixture.ConnectionString).AddInterceptors(new DomainEventOutboxInterceptor());
        if (extra is not null) builder.AddInterceptors(extra);
        await using var db = new MechwareDbContext(builder.Options);
        return await action(new CreditPaymentService(db, new Staff(actor, correlationId ?? "corr-" + Guid.NewGuid().ToString("N")[..8]),
            options ?? new CreditOptions(), TimeProvider.System));
    }

    // ---- data -------------------------------------------------------------------------------------------------------

    private sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers);

    private async Task<Team> TeamAsync(long limitMinor = 10_000_000, int buyers = 1)
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await TestUsers.CreateAsync(fixture));
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Paying Mills Ltd", "TL-B34-1", NextBin(), null, null))).Id;
        foreach (var buyer in buyerIds)
            await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, limitMinor));
        return new Team(company, admin, buyerIds);
    }

    /// <summary>A confirmed credit order of <paramref name="amount"/>, charged <paramref name="daysAgo"/> days ago (so due 30 days after).</summary>
    private async Task<Guid> ChargedOrderAsync(Team t, long amount, int daysAgo)
    {
        await using var db = fixture.CreateContext();
        var order = await CreditTestData.InsertOrderAsync(db, t.Admin, t.Company, amount);
        var company = await db.Companies.SingleAsync(c => c.Id == t.Company);
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return order;
    }

    private static RecordCreditPaymentRequest Pay(long amount, IReadOnlyList<CreditAllocationInput>? allocations = null, string method = "rtgs") =>
        new(method, amount, Today, Ref(), Allocations: allocations);

    private async Task<Guid> RecordAsync(Guid recorder, Guid company, RecordCreditPaymentRequest request) =>
        (await As(recorder, s => s.RecordAsync(company, request, Key()))).Payment.Id;

    private Task<VerifiedCreditPayment> VerifyAsync(Guid verifier, Guid payment, DateOnly? clearedOn = null) =>
        As(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(clearedOn ?? Today)));

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    private async Task<(Company Company, List<CreditLedgerEntry> Lines, CreditPayment Payment)> StateAsync(Guid company, Guid payment)
    {
        await using var db = fixture.CreateContext();
        return (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company),
            await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == company).OrderBy(l => l.Seq).ToListAsync(),
            await db.CreditPayments.AsNoTracking().SingleAsync(p => p.Id == payment));
    }

    private static async Task<string> CodeOf(Func<Task> act) => (await Assert.ThrowsAsync<ConflictException>(act)).Code;

    // ---- §6/§7 the release -------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task EveryLedgerWrite_HasAnAuditRow_WithActorIpAndCorrelationId()
    {
        // CS29, and the §6 order: the named order first, then the oldest due date, the rest on account; one event, one audit row.
        var t = await TeamAsync();
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var older = await ChargedOrderAsync(t, 6_000, daysAgo: 20);
        var newer = await ChargedOrderAsync(t, 5_000, daysAgo: 2);
        var payment = await RecordAsync(recorder, t.Company, Pay(10_000, [new(newer, 3_000)]));
        (await StateAsync(t.Company, payment)).Company.CreditUsedMinor.Should().Be(11_000, "recording releases nothing");

        var result = await As(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)), correlationId: "corr-cs29");

        result.Applied.Should().Equal(new CreditAllocation(newer, 3_000), new CreditAllocation(older, 6_000));
        (result.OnAccountMinor, result.CreditUsedMinor, result.CreditOnAccountMinor).Should().Be((1_000, 2_000, 1_000));
        result.Payment.Should().BeEquivalentTo(new { Status = "verified", VerifiedBy = (Guid?)verifier, ClearedOn = (DateOnly?)Today, Method = "rtgs" });

        var (company, lines, stored) = await StateAsync(t.Company, payment);
        (company.CreditUsedMinor, company.CreditOnAccountMinor).Should().Be((2_000, 1_000));
        stored.Status.Should().Be(CreditPaymentStatus.Verified);
        var verification = lines.Where(l => l.PaymentId == payment).ToList();
        verification.Should().HaveCount(3).And.OnlyContain(l => l.ActorUserId == verifier && l.CorrelationId == "corr-cs29" && l.EventId == result.EventId
            && l.OccurredOn == Today);

        await using var db = fixture.CreateContext();
        var audit = await db.AuditLogs.AsNoTracking().Where(a => a.Action == "credit.payment_verified" && a.EntityId == payment.ToString()).ToListAsync();
        audit.Should().ContainSingle().Which.Should().BeEquivalentTo(new { ActorId = (Guid?)verifier, ActorIp = Ip, CorrelationId = "corr-cs29", EntityName = "credit_payments" });
        using (var before = JsonDocument.Parse(audit[0].OldValuesJson!))
            (before.RootElement.GetProperty("creditUsedMinor").GetInt64(), before.RootElement.GetProperty("creditOnAccountMinor").GetInt64()).Should().Be((11_000, 0));
        using (var after = JsonDocument.Parse(audit[0].NewValuesJson!))
        {
            after.RootElement.GetProperty("eventId").GetGuid().Should().Be(result.EventId);
            (after.RootElement.GetProperty("creditUsedMinor").GetInt64(), after.RootElement.GetProperty("creditOnAccountMinor").GetInt64()).Should().Be((2_000, 1_000));
        }
        (await db.AuditLogs.CountAsync(a => a.Action == "credit.payment_recorded" && a.EntityId == payment.ToString() && a.ActorId == recorder)).Should().Be(1);
        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.AggregateId == payment);
        (outbox.EventType, outbox.Id).Should().Be(("credit.payment_verified", result.EventId));
    }

    [PostgresFact]
    public async Task TwoParallelVerifications_ReleaseCreditOnce()
    {
        // CS1: four verifiers press at once, five times over. The payment row lock lets one through; the others wait, then find the
        // payment verified and get 409 CREDIT_PAYMENT_NOT_RECORDED (not a concurrency error, not a second release).
        var t = await TeamAsync();
        var recorder = await UserAsync();
        var verifiers = new List<Guid>();
        for (var i = 0; i < 4; i++) verifiers.Add(await UserAsync());
        await ChargedOrderAsync(t, 100_000, daysAgo: 10);

        for (var round = 0; round < 5; round++)
        {
            var payment = await RecordAsync(recorder, t.Company, Pay(7_000));
            using var gate = new ManualResetEventSlim(false);
            var racers = verifiers.Select(v => Task.Run(async () =>
            {
                gate.Wait();
                try { await VerifyAsync(v, payment); return "ok"; }
                catch (ConflictException ex) { return ex.Code; }
            })).ToList();
            gate.Set();
            var outcomes = await Task.WhenAll(racers);

            outcomes.Count(o => o == "ok").Should().Be(1, string.Join(",", outcomes));
            outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "CREDIT_PAYMENT_NOT_RECORDED");
            var (_, lines, _) = await StateAsync(t.Company, payment);
            lines.Where(l => l.PaymentId == payment).Sum(l => -l.ReceivableDeltaMinor + l.OnAccountDeltaMinor).Should().Be(7_000);
        }
        var (company, all, _) = await StateAsync(t.Company, await LastPaymentAsync(t.Company));
        company.CreditUsedMinor.Should().Be(100_000 - 5 * 7_000).And.Be(all[^1].ReceivableAfterMinor);
    }

    private async Task<Guid> LastPaymentAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return await db.CreditPayments.Where(p => p.CompanyId == company).OrderByDescending(p => p.CreatedAtUtc).Select(p => p.Id).FirstAsync();
    }

    [PostgresFact]
    public async Task VerifyingAVerifiedPayment_Is409_AndChangesNothing()
    {
        var t = await TeamAsync();
        var (recorder, verifier, other) = (await UserAsync(), await UserAsync(), await UserAsync());
        await ChargedOrderAsync(t, 50_000, daysAgo: 5);
        var payment = await RecordAsync(recorder, t.Company, Pay(20_000));
        await VerifyAsync(verifier, payment);
        var before = await StateAsync(t.Company, payment);

        foreach (var who in new[] { verifier, other })
            (await CodeOf(() => VerifyAsync(who, payment))).Should().Be("CREDIT_PAYMENT_NOT_RECORDED");
        (await CodeOf(() => As(other, s => s.RejectAsync(payment, new RejectCreditPaymentRequest("too late to reject"))))).Should().Be("CREDIT_PAYMENT_NOT_RECORDED");

        var after = await StateAsync(t.Company, payment);
        (after.Company.CreditUsedMinor, after.Company.CreditLedgerSeq, after.Lines.Count).Should().Be((30_000, before.Company.CreditLedgerSeq, before.Lines.Count));
        after.Payment.VerifiedBy.Should().Be(verifier);
        await Assert.ThrowsAsync<KeyNotFoundException>(() => VerifyAsync(verifier, Guid.NewGuid()));
    }

    [PostgresFact]
    public async Task Verify_ByTheRecorder_IsRefused()
    {
        // CS2: the recorder can neither verify nor reject their own entry, whatever permissions they hold.
        var t = await TeamAsync();
        var (recorder, checker) = (await UserAsync(), await UserAsync());
        await ChargedOrderAsync(t, 50_000, daysAgo: 5);
        var payment = await RecordAsync(recorder, t.Company, Pay(20_000));

        (await CodeOf(() => VerifyAsync(recorder, payment))).Should().Be("CREDIT_PAYMENT_FOUR_EYES");
        (await CodeOf(() => As(recorder, s => s.RejectAsync(payment, new RejectCreditPaymentRequest("my own mistake"))))).Should().Be("CREDIT_PAYMENT_FOUR_EYES");
        var (company, lines, stored) = await StateAsync(t.Company, payment);
        (stored.Status, company.CreditUsedMinor, lines.Count).Should().Be((CreditPaymentStatus.Recorded, 50_000, 1));

        (await VerifyAsync(checker, payment)).CreditUsedMinor.Should().Be(30_000);
    }

    [PostgresFact]
    public async Task Allocation_ToAnotherCompanysOrder_IsRefusedLikeAnUnknownOrder()
    {
        // CS7: the planner sees only this company's open receivables, read under its lock; another company's order and an order
        // that does not exist get the same answer, and the other company's ledger is untouched.
        var (a, b) = (await TeamAsync(), await TeamAsync());
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        await ChargedOrderAsync(a, 50_000, daysAgo: 5);
        var theirs = await ChargedOrderAsync(b, 40_000, daysAgo: 5);

        var codes = new List<string>();
        foreach (var target in new[] { theirs, Guid.NewGuid() })
        {
            var payment = await RecordAsync(recorder, a.Company, Pay(10_000, [new(target, 10_000)]));
            var ex = await Assert.ThrowsAsync<ConflictException>(() => VerifyAsync(verifier, payment));
            codes.Add(ex.Code);
            ex.Message.Should().Be($"Order {target} owes nothing on this company's credit.");
            (await StateAsync(a.Company, payment)).Payment.Status.Should().Be(CreditPaymentStatus.Recorded);
        }
        codes.Should().Equal("ALLOCATION_ORDER_NOT_OPEN", "ALLOCATION_ORDER_NOT_OPEN");
        await using var db = fixture.CreateContext();
        (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == b.Company)).CreditUsedMinor.Should().Be(40_000);
        (await db.CreditLedgerEntries.CountAsync(l => l.CompanyId == b.Company)).Should().Be(1);
        (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == a.Company)).CreditUsedMinor.Should().Be(50_000);
    }

    [PostgresFact]
    public async Task Allocation_AboveTheOrdersOutstanding_IsRefused()
    {
        // CS8: never silently clamped. The verifier rejects, the recorder records it again with the right split.
        var t = await TeamAsync();
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var order = await ChargedOrderAsync(t, 5_000, daysAgo: 5);
        await ChargedOrderAsync(t, 9_000, daysAgo: 3);
        await VerifyAsync(verifier, await RecordAsync(recorder, t.Company, Pay(3_000, [new(order, 3_000)])));   // 2,000 left on the order

        var wrong = await RecordAsync(recorder, t.Company, Pay(4_000, [new(order, 4_000)]));
        (await CodeOf(() => VerifyAsync(verifier, wrong))).Should().Be("ALLOCATION_EXCEEDS_OUTSTANDING");
        (await StateAsync(t.Company, wrong)).Company.CreditUsedMinor.Should().Be(11_000);

        var rejected = await As(verifier, s => s.RejectAsync(wrong, new RejectCreditPaymentRequest("order only owes 2,000; re-record")));
        (rejected.Status, rejected.RejectedBy, rejected.DecisionReason).Should().Be(("rejected", verifier, "order only owes 2,000; re-record"));
        var right = await RecordAsync(recorder, t.Company, Pay(4_000, [new(order, 2_000)]));
        var result = await VerifyAsync(verifier, right);
        (result.CreditUsedMinor, result.OnAccountMinor).Should().Be((7_000, 0));
        await using var db = fixture.CreateContext();
        (await db.AuditLogs.CountAsync(a => a.Action == "credit.payment_rejected" && a.EntityId == wrong.ToString() && a.ActorId == verifier)).Should().Be(1);
    }

    [PostgresFact]
    public async Task ClearedOn_BeforeTheChequeDate_IsRefused()
    {
        // CS11 at the service: the clearing date lies between the cheque date and today; a post-dated cheque waits for its date.
        var t = await TeamAsync();
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        await ChargedOrderAsync(t, 50_000, daysAgo: 5);
        var cheque = await RecordAsync(recorder, t.Company,
            new RecordCreditPaymentRequest("cheque", 10_000, Today.AddDays(-3), "CHQ" + Ref(), "Sonali Bank", ChequeDate: Today.AddDays(-1)));

        foreach (var bad in new[] { Today.AddDays(-2), Today.AddDays(1) })
            (await Assert.ThrowsAsync<ValidationException>(() => VerifyAsync(verifier, cheque, bad))).Message.Should().Contain("cleared");
        (await StateAsync(t.Company, cheque)).Payment.Status.Should().Be(CreditPaymentStatus.Recorded);
        (await VerifyAsync(verifier, cheque, Today.AddDays(-1))).Payment.ClearedOn.Should().Be(Today.AddDays(-1));

        var postDated = await RecordAsync(recorder, t.Company,
            new RecordCreditPaymentRequest("cheque", 10_000, Today, "PDC" + Ref(), "Sonali Bank", ChequeDate: Today.AddDays(10)));
        (await CodeOf(() => VerifyAsync(verifier, postDated))).Should().Be("CREDIT_PAYMENT_CHEQUE_NOT_DUE");
        (await StateAsync(t.Company, postDated)).Company.CreditUsedMinor.Should().Be(40_000);
    }

    [PostgresFact]
    public async Task MobileWalletPayment_StoresTheTransactionIdOnly()
    {
        // CS31: a bKash/Nagad payment keeps the TrxID; a wallet phone number is refused before anything is stored.
        var t = await TeamAsync();
        var recorder = await UserAsync();
        foreach (var phone in new[] { "01711-123456", "+880 1711 123456" })
            await Assert.ThrowsAsync<ValidationException>(() => As(recorder, s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest("bkash", 5_000, Today, phone), Key())));

        var trx = "BK" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var recorded = (await As(recorder, s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest("nagad", 5_000, Today, trx.ToLowerInvariant()), Key()))).Payment;
        (recorded.Method, recorded.Reference, recorded.DrawerBank).Should().Be(("nagad", trx, null));
        await using var db = fixture.CreateContext();
        (await db.CreditPayments.CountAsync(p => p.CompanyId == t.Company)).Should().Be(1);
    }

    [PostgresFact]
    public async Task ASuspendedCompany_StillSeesItsStatement_AndCanBePaid()
    {
        // CS24, the payment half (the statement is B3.9): a suspended company's debt can still be paid and released.
        // Unknown, pending and rejected companies all get the same 404.
        var t = await TeamAsync();
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        await ChargedOrderAsync(t, 50_000, daysAgo: 40);
        await using (var db = fixture.CreateContext()) await new CompanyService(db).AdminSuspendAsync(t.Company);

        var payment = await RecordAsync(recorder, t.Company, Pay(50_000));
        (await VerifyAsync(verifier, payment)).CreditUsedMinor.Should().Be(0);

        Guid pending, rejected;
        await using (var db = fixture.CreateContext())
        {
            var companies = new CompanyService(db);
            pending = (await companies.RegisterAsync(t.Admin, new RegisterCompanyRequest("Pending Ltd", "TL-B34-P", NextBin(), null, null))).Id;
            rejected = (await companies.RegisterAsync(t.Admin, new RegisterCompanyRequest("Rejected Ltd", "TL-B34-R", NextBin(), null, null))).Id;
            await companies.AdminRejectAsync(rejected, new RejectCompanyRequest("Trade licence could not be verified"));
        }
        foreach (var company in new[] { pending, rejected, Guid.NewGuid() })
            (await Assert.ThrowsAsync<KeyNotFoundException>(() => As(recorder, s => s.RecordAsync(company, Pay(1_000), Key())))).Message.Should().Be("Company not found.");
    }

    [PostgresFact]
    public async Task AFailureAfterTheLedgerInsert_RollsBackEverything()
    {
        // CS17: the ledger insert reaches the database, then the command fails. Nothing of the verification survives: no line,
        // no cached total, no status, no audit row, no outbox event; verifying again later works once.
        var t = await TeamAsync();
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        await ChargedOrderAsync(t, 50_000, daysAgo: 5);
        var payment = await RecordAsync(recorder, t.Company, Pay(20_000));
        var failing = new FailAfterLedgerInsert();

        (await Assert.ThrowsAsync<DbUpdateException>(() =>
            As(verifier, s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)), extra: failing))).InnerException.Should().BeOfType<InvalidOperationException>();
        failing.Fired.Should().BeTrue("the ledger insert was sent before the failure");

        var (company, lines, stored) = await StateAsync(t.Company, payment);
        (stored.Status, company.CreditUsedMinor, company.CreditLedgerSeq, lines.Count).Should().Be((CreditPaymentStatus.Recorded, 50_000, 1, 1));
        await using (var db = fixture.CreateContext())
        {
            (await db.AuditLogs.CountAsync(a => a.Action == "credit.payment_verified" && a.EntityId == payment.ToString())).Should().Be(0);
            (await db.OutboxMessages.CountAsync(m => m.AggregateId == payment)).Should().Be(0);
        }

        (await VerifyAsync(verifier, payment)).CreditUsedMinor.Should().Be(30_000);
        (await StateAsync(t.Company, payment)).Lines.Count(l => l.PaymentId == payment).Should().Be(1);
    }

    private sealed class FailAfterLedgerInsert : DbCommandInterceptor
    {
        public bool Fired { get; private set; }

        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result,
            CancellationToken cancellationToken = default)
        {
            if (!command.CommandText.Contains("INSERT INTO credit_ledger_entries", StringComparison.Ordinal)) return ValueTask.FromResult(result);
            Fired = true;
            throw new InvalidOperationException("simulated crash after the ledger insert");
        }
    }

    [PostgresFact]
    public async Task StepUp_IsAskedForEveryVerification_UnlessTheOwnerSetsAThreshold()
    {
        // Owner decision 3: the default asks for a code on every verification; the alternative asks only from the threshold.
        var t = await TeamAsync();
        var recorder = await UserAsync();
        var (small, large) = (await RecordAsync(recorder, t.Company, Pay(9_999)), await RecordAsync(recorder, t.Company, Pay(10_000)));
        var threshold = new CreditOptions { StepUpOnEveryVerification = false, VerifyStepUpFromMinor = 10_000 };

        (await As(recorder, s => s.VerificationNeedsStepUpAsync(small))).Should().BeTrue();
        (await As(recorder, s => s.VerificationNeedsStepUpAsync(small), threshold)).Should().BeFalse();
        (await As(recorder, s => s.VerificationNeedsStepUpAsync(large), threshold)).Should().BeTrue();
        (await As(recorder, s => s.VerificationNeedsStepUpAsync(Guid.NewGuid()), threshold)).Should().BeTrue("an unknown payment never skips the code");
    }

    // ---- CS15: verifications and new credit orders at once -----------------------------------------------------------

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<PlacedOrder> PlaceAsync(Guid buyer, Guid company, Guid variant)
    {
        await using var db = fixture.CreateContext();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db));
        return await orders.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(variant, 1)], null, company, PayOnCredit: true));
    }

    [PostgresFact]
    public async Task ParallelCreditOrdersAndVerifications_NeverExceedTheLimit_AndTheLedgerAddsUp()
    {
        // 30 buyers each place a 100,000 credit order while 10 payments of 100,000 are verified, all at once, against a limit of
        // 1,000,000 that is already fully used. Both sides take the company row lock: every verification succeeds, an order wins
        // only with credit a committed verification freed, and the ledger chain ends exactly at the company's cached totals.
        const long Unit = 100_000;
        var t = await TeamAsync(limitMinor: 10 * Unit, buyers: 30);
        var (recorder, verifier) = (await UserAsync(), await UserAsync());
        var variants = new List<Guid>();
        await using (var db = fixture.CreateContext())
            for (var i = 0; i < 30; i++) variants.Add((await CatalogTestData.SeedVariantAsync(db, 5, priceMinor: Unit)).VariantId);
        for (var i = 0; i < 10; i++) await PlaceAsync(t.Buyers[i], t.Company, variants[i]);
        var payments = new List<Guid>();
        for (var i = 0; i < 10; i++) payments.Add(await RecordAsync(recorder, t.Company, Pay(Unit)));
        (await StateAsync(t.Company, payments[0])).Company.CreditUsedMinor.Should().Be(10 * Unit, "the limit is fully used before the race");

        using var gate = new ManualResetEventSlim(false);
        var orderTasks = t.Buyers.Select((buyer, i) => Task.Run(async () =>
        {
            gate.Wait();
            try { return (await PlaceAsync(buyer, t.Company, variants[i])).OrderId.ToString(); }
            catch (ConflictException ex) { return ex.Code; }
        })).ToList();
        var verifyTasks = payments.Select(p => Task.Run(async () =>
        {
            gate.Wait();
            return await VerifyAsync(verifier, p);
        })).ToList();
        gate.Set();
        var outcomes = await Task.WhenAll(orderTasks);
        await Task.WhenAll(verifyTasks);   // every verification must succeed: a lost update or a broken chain throws here

        var won = outcomes.Count(o => Guid.TryParse(o, out _));
        outcomes.Where(o => !Guid.TryParse(o, out _)).Should().OnlyContain(o => o == "CREDIT_LIMIT_EXCEEDED");
        won.Should().BeLessThanOrEqualTo(10, "only the 10 verified payments freed credit");
        var (company, lines, _) = await StateAsync(t.Company, payments[0]);
        company.CreditUsedMinor.Should().Be(won * Unit, "10 charged + won charged − 10 paid").And.BeLessThanOrEqualTo(company.CreditLimitMinor);
        company.CreditUsedMinor.Should().Be(lines.Sum(l => l.ReceivableDeltaMinor)).And.Be(lines[^1].ReceivableAfterMinor);
        (company.CreditOnAccountMinor, company.CreditLedgerSeq).Should().Be((0, lines.Count));
        lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, lines.Count).Select(i => (long)i));
        lines.Count(l => l.Kind == CreditLedgerKind.Charge).Should().Be(10 + won);
        lines.Count(l => l.Kind == CreditLedgerKind.PaymentApplied).Should().BeGreaterThanOrEqualTo(10);
    }
}
