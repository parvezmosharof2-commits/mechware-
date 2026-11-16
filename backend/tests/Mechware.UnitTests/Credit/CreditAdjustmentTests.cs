using FluentAssertions;
using Mechware.Application.Credit;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;

namespace Mechware.UnitTests.Credit;

/// <summary>
/// ADR-0010 §8.3 (B3.6, threat model CS20, CS21), without a database: the one line an approved adjustment posts, and every cap it
/// meets: a write-off at most what the order owes, a payout or a negative correction at most what its source holds, a positive
/// correction only gives back what corrections took, a payout of money that can still bounce waits after its clearing, and no
/// adjustment ever raises a receivable.
/// </summary>
public class CreditAdjustmentTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static readonly Guid Requester = Guid.NewGuid(), Recorder = Guid.NewGuid(), Verifier = Guid.NewGuid(), Approver = Guid.NewGuid();
    private static readonly CreditPaymentRules Rules = new CreditOptions().PaymentRules();
    private const int Wait = 30;

    private static CreditEventInfo On(DateOnly day, string reason = "approved adjustment") => new(day, Guid.NewGuid(), Approver, "corr-adjust", reason);

    private sealed class Book
    {
        public readonly Company Company;
        public readonly List<CreditLedgerEntry> Lines = [];
        private readonly CreditLedger _ledger;

        public Book()
        {
            Company = Company.Register(Guid.NewGuid(), "Adjusted Traders Ltd", "TL-2020-5678", "0012345678902", null, null);
            Company.Activate();
            Company.SetCredit(CreditTerms.Net30, new Money(10_000_000, Currency.BDT));
            _ledger = new CreditLedger(Company, []);
        }

        public Guid Charge(long amount)
        {
            var order = Guid.NewGuid();
            Lines.Add(_ledger.Charge(order, amount, Now, Now).Entry);
            return order;
        }

        public void Post(CreditPosting p) => Lines.Add(_ledger.Post(p, Now).Entry);

        /// <summary>A verified payment (cleared today) whose money pays <paramref name="applied"/> and leaves the rest on account.</summary>
        public CreditPayment Pay(long amount, CreditPaymentMethod method = CreditPaymentMethod.Beftn, params (Guid Order, long Amount)[] applied)
        {
            var payment = CreditPayment.Record(Guid.NewGuid(), new CreditPaymentInput(Company.Id, method, amount,
                method == CreditPaymentMethod.Cash ? null : "TRX" + Guid.NewGuid().ToString("N")[..12], null, Today), Recorder,
                "key-" + Guid.NewGuid().ToString("N"), new string('a', 64), Rules, Now);
            payment.Verify(Verifier, Today, Now);
            var plan = new CreditAllocationPlan(applied.Select(a => new CreditAllocation(a.Order, a.Amount)).ToList(), amount - applied.Sum(a => a.Amount));
            foreach (var p in plan.Postings(payment.Id, Today, Guid.NewGuid(), null, null)) Post(p);
            return payment;
        }

        public CreditAdjustment Ask(CreditAdjustmentKind kind, long amount, Guid? order = null, CreditPayment? payment = null, Guid? note = null) =>
            CreditAdjustment.Request(Guid.NewGuid(), new CreditAdjustmentInput(Company.Id, kind, amount, "agreed with the customer in writing",
                order, payment?.Id, note, kind == CreditAdjustmentKind.OnAccountPayout ? "MW-OUT-" + Guid.NewGuid().ToString("N")[..8] : null),
                Requester, Rules.MaxPaymentMinor, Now);

        /// <summary>Approves and posts, as the service does; returns the posted line.</summary>
        public CreditLedgerEntry Approve(CreditAdjustment a, CreditPayment? source = null, DateOnly? day = null, int wait = Wait)
        {
            var posting = a.Posting(Lines, source, wait, On(day ?? Today));
            a.Approve(Approver, Now, source);
            Post(posting);
            return Lines[^1];
        }

        public long OnAccountOf(Guid source) => Lines.Where(l => l.PaymentId == source || l.CreditNoteId == source).Sum(l => l.OnAccountDeltaMinor);
    }

    private static string CodeOf(Action act) => act.Should().Throw<ConflictException>().Which.Code;

    // ---- CS20 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void WriteOff_AboveTheOutstanding_IsRefused()
    {
        var b = new Book();
        var order = b.Charge(50_000);
        b.Pay(20_000, CreditPaymentMethod.Beftn, (order, 20_000));

        // Posting alone is what the request checks (an early answer); the ledger repeats the cap when the approval posts.
        CodeOf(() => b.Ask(CreditAdjustmentKind.WriteOff, 30_001, order).Posting(b.Lines, null, Wait, On(Today))).Should().Be("CREDIT_EXCEEDS_RECEIVABLE", "never clamped");
        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.WriteOff, 30_001, order))).Should().Be("CREDIT_EXCEEDS_RECEIVABLE");
        var writeOff = b.Ask(CreditAdjustmentKind.WriteOff, 30_000, order);
        var line = b.Approve(writeOff);

        line.Should().BeEquivalentTo(new
        {
            Kind = CreditLedgerKind.WriteOff,
            ReceivableDeltaMinor = -30_000L,
            OnAccountDeltaMinor = 0L,
            OrderId = (Guid?)order,
            PaymentId = (Guid?)null,
            AdjustmentId = (Guid?)writeOff.Id,
            IdempotencyKey = $"adjust:{writeOff.Id:N}",
            ActorUserId = (Guid?)Approver,
            CorrelationId = "corr-adjust",
            OccurredOn = Today
        });
        (b.Company.CreditUsedMinor, new CreditLedger(b.Company, b.Lines).BalanceOf(order)).Should().Be((0, new CreditOrderBalance(50_000, 30_000, 0)));
        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.WriteOff, 1, order))).Should().Be("CREDIT_EXCEEDS_RECEIVABLE", "nothing is owed any more");
        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.WriteOff, 1, Guid.NewGuid()))).Should().Be("CREDIT_ADJUSTMENT_TARGET_NOT_FOUND");
    }

    [Fact]
    public void AReceivableIncrease_ByAdjustment_IsRefused()
    {
        // A debt is created only by an order (§2.2): a negative write-off is not a request, and a line that would raise a receivable
        // in an adjustment's name is refused by the ledger whatever its kind (the database CHECK says the same).
        var b = new Book();
        var order = b.Charge(10_000);
        b.Invoking(x => x.Ask(CreditAdjustmentKind.WriteOff, -5_000, order)).Should().Throw<ValidationException>();
        b.Invoking(x => x.Ask(CreditAdjustmentKind.OnAccountCorrection, 5_000, order)).Should().Throw<ValidationException>();
        foreach (var kind in new[] { CreditLedgerKind.WriteOff, CreditLedgerKind.OnAccountCorrection, CreditLedgerKind.PaymentReversed })
            b.Invoking(x => x.Post(new CreditPosting(kind, 5_000, 0, "adjust:raise:" + kind, Today, order, AdjustmentId: Guid.NewGuid())))
                .Should().Throw<ValidationException>(kind.ToString());
        b.Company.CreditUsedMinor.Should().Be(10_000);
    }

    [Fact]
    public void Adjustment_ApprovedByItsRequester_OrBySomeoneWhoHandledItsPayment_IsRefused()
    {
        var b = new Book();
        var payment = b.Pay(10_000, CreditPaymentMethod.Cash);
        foreach (var who in new[] { Requester, Recorder, Verifier })
            CodeOf(() => b.Ask(CreditAdjustmentKind.OnAccountCorrection, -1_000, payment: payment).Approve(who, Now, payment))
                .Should().Be("CREDIT_ADJUSTMENT_FOUR_EYES");
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, -1_000, payment: payment), payment).OnAccountDeltaMinor.Should().Be(-1_000);
    }

    // ---- CS21 ------------------------------------------------------------------------------------------------------------

    [Fact]
    public void Payout_AboveTheOnAccountBalance_IsRefused()
    {
        var b = new Book();
        var order = b.Charge(30_000);
        var cash = b.Pay(100_000, CreditPaymentMethod.Cash, (order, 30_000));   // 70,000 on account

        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.OnAccountPayout, 70_001, payment: cash), cash)).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");
        var payout = b.Ask(CreditAdjustmentKind.OnAccountPayout, 70_000, payment: cash);
        b.Approve(payout, cash).Should().BeEquivalentTo(new
        {
            Kind = CreditLedgerKind.OnAccountPayout,
            ReceivableDeltaMinor = 0L,
            OnAccountDeltaMinor = -70_000L,
            PaymentId = (Guid?)cash.Id,
            AdjustmentId = (Guid?)payout.Id,
            OrderId = (Guid?)null
        });
        (b.OnAccountOf(cash.Id), b.Company.CreditOnAccountMinor, b.Company.CreditUsedMinor).Should().Be((0, 0, 0));
        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.OnAccountPayout, 1, payment: cash), cash)).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");
        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.OnAccountPayout, 1, note: Guid.NewGuid()))).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");
    }

    [Fact]
    public void APayout_OfMoneyThatCanStillBounce_WaitsTheClearingPeriod_CashDoesNot()
    {
        var b = new Book();
        var transfer = b.Pay(50_000);                                 // cleared today, all on account
        var cash = b.Pay(10_000, CreditPaymentMethod.Cash);

        foreach (var day in new[] { Today, Today.AddDays(Wait - 1) })
            CodeOf(() => b.Ask(CreditAdjustmentKind.OnAccountPayout, 1_000, payment: transfer).Posting(b.Lines, transfer, Wait, On(day)))
                .Should().Be("CREDIT_PAYOUT_TOO_EARLY", day.ToString());
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountPayout, 1_000, payment: transfer), transfer, Today.AddDays(Wait)).OnAccountDeltaMinor.Should().Be(-1_000);
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountPayout, 1_000, payment: transfer), transfer, Today, wait: 0).OnAccountDeltaMinor.Should().Be(-1_000);
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountPayout, 10_000, payment: cash), cash).OnAccountDeltaMinor.Should().Be(-10_000, "cash cannot bounce");
        // A correction takes money away (or gives back what a correction took): it never needs to wait.
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, -500, payment: transfer), transfer).OnAccountDeltaMinor.Should().Be(-500);
        b.OnAccountOf(transfer.Id).Should().Be(47_500);
    }

    [Theory]
    [InlineData(-1, false)]
    [InlineData(0, true)]
    [InlineData(30, true)]
    [InlineData(365, true)]
    [InlineData(366, false)]
    public void PayoutAfterClearingDays_OutsideZeroTo365_StopsTheHost(int days, bool ok)
    {
        var act = () => CreditOptions.Validated(new CreditOptions { PayoutAfterClearingDays = days });
        if (ok) act.Should().NotThrow();
        else act.Should().Throw<InvalidOperationException>().WithMessage("Credit:PayoutAfterClearingDays*");
        new CreditOptions().PayoutAfterClearingDays.Should().Be(30, "the default pending the owner (decision 7)");
    }

    // ---- corrections -----------------------------------------------------------------------------------------------------

    [Fact]
    public void ACorrection_NeverCreatesMoney_ItOnlyGivesBackWhatACorrectionTook()
    {
        var b = new Book();
        var payment = b.Pay(10_000);

        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, 1, payment: payment), payment)).Should().Be("CREDIT_CORRECTION_EXCEEDS_TAKEN");
        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, -10_001, payment: payment), payment)).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, -4_000, payment: payment), payment);
        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, 4_001, payment: payment), payment)).Should().Be("CREDIT_CORRECTION_EXCEEDS_TAKEN");
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, 4_000, payment: payment), payment).OnAccountDeltaMinor.Should().Be(4_000);
        CodeOf(() => b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, 1, payment: payment), payment)).Should().Be("CREDIT_CORRECTION_EXCEEDS_TAKEN");
        (b.OnAccountOf(payment.Id), b.Company.CreditOnAccountMinor).Should().Be((10_000, 10_000));
    }

    [Fact]
    public void AnAdjustment_OfABouncedOrAnotherCompanysPayment_IsRefused()
    {
        var b = new Book();
        var payment = b.Pay(10_000);
        var correction = b.Ask(CreditAdjustmentKind.OnAccountCorrection, -1_000, payment: payment);
        correction.Invoking(a => a.Posting(b.Lines, null, Wait, On(Today))).Should().Throw<InvalidOperationException>("the source must be passed");
        correction.Invoking(a => a.Posting(b.Lines, b.Pay(5_000), Wait, On(Today))).Should().Throw<InvalidOperationException>("and only that source");

        var other = new Book().Pay(10_000);
        var foreign = CreditAdjustment.Request(Guid.NewGuid(), new CreditAdjustmentInput(b.Company.Id, CreditAdjustmentKind.OnAccountCorrection, -1_000,
            "mis-posted deposit slip", PaymentId: other.Id), Requester, Rules.MaxPaymentMinor, Now);
        CodeOf(() => foreign.Posting(b.Lines, other, Wait, On(Today))).Should().Be("CREDIT_ADJUSTMENT_TARGET_NOT_FOUND");

        foreach (var p in CreditReversals.Bounce(payment.Id, b.Lines, On(Today, "cheque returned"))) b.Post(p);
        payment.Bounce(Verifier, "cheque returned", Now);
        CodeOf(() => correction.Posting(b.Lines, payment, Wait, On(Today))).Should().Be("CREDIT_ADJUSTMENT_SOURCE_NOT_VERIFIED");
    }

    [Fact]
    public void ABounce_AfterAPayoutOrACorrection_ReversesOnlyWhatIsLeft()
    {
        // ADR-0010 B3.6 notes: a payout is money gone to the customer's bank; a later bounce cannot take it back, which is why a
        // payout of money that can still bounce waits. What a bounce reverses is the payment's money still in the ledger.
        var b = new Book();
        var order = b.Charge(20_000);
        var payment = b.Pay(50_000, CreditPaymentMethod.Beftn, (order, 20_000));   // 30,000 on account
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountCorrection, -5_000, payment: payment), payment);
        b.Approve(b.Ask(CreditAdjustmentKind.OnAccountPayout, 10_000, payment: payment), payment, Today.AddDays(Wait));

        foreach (var p in CreditReversals.Bounce(payment.Id, b.Lines, On(Today.AddDays(Wait), "returned by the bank"))) b.Post(p);

        (b.OnAccountOf(payment.Id), b.Company.CreditOnAccountMinor, b.Company.CreditUsedMinor).Should().Be((0, 0, 20_000));
        b.Lines[^1].OnAccountDeltaMinor.Should().Be(-15_000, "only what was still on account");
    }

    [Fact]
    public void RandomAdjustments_WithPaymentsBouncesAndReductions_NeverRaiseADebt_NorTakeASourceBelowZero()
    {
        for (var seed = 1; seed <= 500; seed++)
        {
            var rnd = new Random(seed);
            var b = new Book();
            var orders = new List<Guid>();
            var payments = new List<CreditPayment>();
            long charged = 0, adjustedDebt = 0;
            for (var step = 0; step < 30; step++)
            {
                var kind = (CreditAdjustmentKind)rnd.Next(1, 4);
                try
                {
                    switch (rnd.Next(6))
                    {
                        case 0:
                            orders.Add(b.Charge(rnd.Next(1, 400)));
                            charged += b.Lines[^1].ReceivableDeltaMinor;
                            break;
                        case 1:
                            var now = new CreditLedger(b.Company, b.Lines);
                            var open = orders.Select(o => new CreditOpenCharge(o, now.BalanceOf(o).ReceivableMinor, Today, Now)).Where(c => c.ReceivableMinor > 0);
                            var paid = rnd.Next(1, 300);
                            var plan = rnd.Next(2) == 0 ? new CreditAllocationPlan([], paid) : CreditAllocationPlanner.Plan(paid, open);
                            payments.Add(b.Pay(paid, rnd.Next(3) == 0 ? CreditPaymentMethod.Cash : CreditPaymentMethod.Rtgs,
                                plan.Allocations.Select(a => (a.OrderId, a.AmountMinor)).ToArray()));
                            break;
                        case 2 when payments.Count > 0:
                            var victim = payments[rnd.Next(payments.Count)];
                            if (victim is { Status: CreditPaymentStatus.Verified, Method: not CreditPaymentMethod.Cash })
                            {
                                foreach (var p in CreditReversals.Bounce(victim.Id, b.Lines, On(Today, "returned"))) b.Post(p);
                                victim.Bounce(Verifier, "returned", Now);
                            }
                            break;
                        case 3 when orders.Count > 0:
                            var o2 = orders[rnd.Next(orders.Count)];
                            foreach (var p in CreditReversals.Reduce(o2, new CreditLedger(b.Company, b.Lines).BalanceOf(o2), b.Lines, null, $"cancel:{o2:N}", null,
                                On(Today, "cancelled")).Postings) b.Post(p);
                            break;
                        default:
                            var source = payments.Count > 0 ? payments[rnd.Next(payments.Count)] : null;
                            var amount = rnd.Next(1, 200) * (kind == CreditAdjustmentKind.OnAccountCorrection && rnd.Next(2) == 0 ? -1 : 1);
                            var a = kind == CreditAdjustmentKind.WriteOff
                                ? orders.Count > 0 ? b.Ask(kind, amount, orders[rnd.Next(orders.Count)]) : null
                                : source is null ? null : b.Ask(kind, amount, payment: source);
                            if (a is null) break;
                            var line = b.Approve(a, a.PaymentId is null ? null : source, Today.AddDays(rnd.Next(0, 2) * Wait));
                            line.ReceivableDeltaMinor.Should().BeLessThanOrEqualTo(0, $"seed {seed}: an adjustment never raises a debt");
                            adjustedDebt -= line.ReceivableDeltaMinor;
                            break;
                    }
                }
                catch (ConflictException ex) when (ex.Code is "CREDIT_EXCEEDS_RECEIVABLE" or "CREDIT_ON_ACCOUNT_INSUFFICIENT" or "CREDIT_CORRECTION_EXCEEDS_TAKEN"
                    or "CREDIT_PAYOUT_TOO_EARLY" or "CREDIT_ADJUSTMENT_SOURCE_NOT_VERIFIED" or "CREDIT_REDUCTION_EXCEEDS_CHARGE")
                { }

                var ledger = new CreditLedger(b.Company, b.Lines);
                foreach (var o in orders)
                {
                    var x = ledger.BalanceOf(o);
                    (x.ReceivableMinor >= 0 && x.PaidMinor >= 0 && x.ReducedMinor <= x.ChargeMinor).Should().BeTrue($"seed {seed}: I4/I6 on {o}");
                }
                CreditOnAccount.Sources(b.Lines).Sum(s => s.OnAccountMinor).Should().Be(b.Company.CreditOnAccountMinor, $"seed {seed}: I2 per source");
                b.Company.CreditUsedMinor.Should().Be(orders.Sum(o => ledger.BalanceOf(o).ReceivableMinor), $"seed {seed}: I1");
                foreach (var p in payments.Where(p => p.Status == CreditPaymentStatus.Bounced))
                    b.OnAccountOf(p.Id).Should().Be(0, $"seed {seed}: a bounced payment holds nothing");
                adjustedDebt.Should().BeLessThanOrEqualTo(charged, $"seed {seed}: write-offs never exceed the charges");
            }
        }
    }
}
