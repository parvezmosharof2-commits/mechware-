using FluentAssertions;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;

namespace Mechware.UnitTests.Credit;

/// <summary>
/// ADR-0010 §8.1 (bounce) and §8.2 (reduction), threat model CS12 and CS19, without a database: which lines undo money, in whose
/// name, and that together they never break I4 (0 ≤ receivable ≤ charge), I5 (a payment's money adds up to its amount, 0 after a
/// bounce) or I6 (reductions ≤ charge).
/// </summary>
public class CreditReversalsTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static readonly CreditEventInfo Info = new(Today, Guid.NewGuid(), null, "corr-unit", "test reason");

    /// <summary>One company's ledger kept in memory: every line posted, through <see cref="CreditLedger"/> as the services do.</summary>
    private sealed class Book
    {
        public readonly Company Company;
        public readonly List<CreditLedgerEntry> Lines = [];
        private readonly CreditLedger _ledger;

        public Book()
        {
            Company = Company.Register(Guid.NewGuid(), "Square Textiles Ltd", "TL-2020-1234", "0012345678901", null, null);
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

        public void Post(CreditPosting p)
        {
            var r = _ledger.Post(p, Now);
            if (!r.AlreadyApplied) Lines.Add(r.Entry);
        }

        public Guid Pay(long onAccount, params (Guid Order, long Amount)[] applied)
        {
            var payment = Guid.NewGuid();
            var plan = new CreditAllocationPlan(applied.Select(a => new CreditAllocation(a.Order, a.Amount)).ToList(), onAccount);
            foreach (var p in plan.Postings(payment, Today, Guid.NewGuid(), null, null)) Post(p);
            return payment;
        }

        public void ApplyOnAccount(Guid source, Guid order, long x) =>
            Post(new CreditPosting(CreditLedgerKind.OnAccountApplied, -x, -x, $"apply:{Guid.NewGuid():N}", Today, order, source));

        public CreditReductionPlan Reduce(Guid order, long? amount, string key, Guid? note = null)
        {
            var plan = CreditReversals.Reduce(order, _ledger.BalanceOf(order), Lines, amount, key, note, Info);
            foreach (var p in plan.Postings) Post(p);
            return plan;
        }

        public IReadOnlyList<CreditPosting> Bounce(Guid payment)
        {
            var postings = CreditReversals.Bounce(payment, Lines, Info);
            foreach (var p in postings) Post(p);
            return postings;
        }

        public CreditOrderBalance Of(Guid order) => _ledger.BalanceOf(order);

        /// <summary>What a payment's money is now: paid on orders + still on account (I5).</summary>
        public long MoneyOf(Guid payment) => Lines.Where(l => l.PaymentId == payment)
            .Sum(l => (l.OrderId is null ? 0 : l.Kind == CreditLedgerKind.ChargeReduced ? -l.OnAccountDeltaMinor : -l.ReceivableDeltaMinor) + l.OnAccountDeltaMinor);
    }

    // ---- §8.2 reductions ---------------------------------------------------------------------------------------------

    [Fact]
    public void ThePaidPart_OfACancelledOrder_GoesOnAccount()
    {
        // The ADR's example: charge 100, credit note 20, paid 30, so 50 owed. Cancelling reduces by the remaining 80: the debt
        // falls by 50 and the 30 already paid goes on account in the payment's name.
        var b = new Book();
        var order = b.Charge(100);
        b.Reduce(order, 20, "creditnote:cn1", Guid.NewGuid());
        var payment = b.Pay(0, (order, 30));

        var plan = b.Reduce(order, null, $"cancel:{order:N}");

        (plan.ReceivableReleasedMinor, plan.MovedOnAccountMinor, plan.AlreadyApplied).Should().Be((50, 30, false));
        plan.Postings.Should().HaveCount(2);
        plan.Postings[1].Should().BeEquivalentTo(new { Kind = CreditLedgerKind.ChargeReduced, ReceivableDeltaMinor = 0L, OnAccountDeltaMinor = 30L, PaymentId = (Guid?)payment });
        b.Of(order).Should().Be(new CreditOrderBalance(100, 100, 0));
        (b.Company.CreditUsedMinor, b.Company.CreditOnAccountMinor).Should().Be((0, 30));
        b.MoneyOf(payment).Should().Be(30, "the money is still the payment's, now on account");
    }

    [Fact]
    public void ReducingACharge_TwiceWithTheSameKey_AppliesOnce()
    {
        var b = new Book();
        var order = b.Charge(100);
        b.Reduce(order, 40, "creditnote:cn1", Guid.NewGuid()).AlreadyApplied.Should().BeFalse();
        var lines = b.Lines.Count;

        var again = b.Reduce(order, 40, "creditnote:cn1", Guid.NewGuid());
        (again.AlreadyApplied, again.ReceivableReleasedMinor, again.Postings.Count, b.Lines.Count).Should().Be((true, 40, 0, lines));
        Assert.Throws<ConflictException>(() => b.Reduce(order, 41, "creditnote:cn1")).Code.Should().Be("CREDIT_REDUCTION_CONFLICT");

        b.Reduce(order, null, "cancel:o1").ReceivableReleasedMinor.Should().Be(60);
        b.Reduce(order, null, "cancel:o1").Should().BeEquivalentTo(new { AlreadyApplied = true, ReceivableReleasedMinor = 60L, MovedOnAccountMinor = 0L });
        b.Of(order).Should().Be(new CreditOrderBalance(100, 100, 0));
    }

    [Fact]
    public void CreditNoteAndCancellation_TogetherNeverExceedTheCharge()
    {
        var b = new Book();
        var order = b.Charge(100);
        b.Pay(0, (order, 60));
        b.Reduce(order, 30, "creditnote:cn1", Guid.NewGuid());

        Assert.Throws<ConflictException>(() => b.Reduce(order, 71, "creditnote:cn2", Guid.NewGuid())).Code.Should().Be("CREDIT_REDUCTION_EXCEEDS_CHARGE");
        b.Reduce(order, null, "cancel:o1").Should().BeEquivalentTo(new { ReceivableReleasedMinor = 10L, MovedOnAccountMinor = 60L });
        Assert.Throws<ConflictException>(() => b.Reduce(order, 1, "creditnote:cn3", Guid.NewGuid())).Code.Should().Be("CREDIT_REDUCTION_EXCEEDS_CHARGE");
        b.Reduce(order, null, "cancel:again").Should().BeEquivalentTo(new { ReceivableReleasedMinor = 0L, MovedOnAccountMinor = 0L, AlreadyApplied = false },
            "nothing is left to reduce; no line is written");
        b.Of(order).Should().Be(new CreditOrderBalance(100, 100, 0));
    }

    [Fact]
    public void TheMovedMoney_IsAttributedToThePayingSources_NewestFirst()
    {
        // Paid 30 by A, then 40 by B (B's through on-account money it brought earlier): owed 30. A credit note of 80 releases the
        // 30 owed and moves 50 on account: 40 in B's name (newest), 10 in A's.
        var b = new Book();
        var order = b.Charge(100);
        var other = b.Charge(10);
        var a = b.Pay(0, (order, 30));
        var src = b.Pay(40, (other, 10));
        b.ApplyOnAccount(src, order, 40);

        var plan = b.Reduce(order, 80, "creditnote:cn1", Guid.NewGuid());

        plan.Postings.Select(p => (p.ReceivableDeltaMinor, p.OnAccountDeltaMinor, p.PaymentId))
            .Should().Equal((-30L, 0L, (Guid?)null), (0L, 40L, src), (0L, 10L, a));
        (b.MoneyOf(a), b.MoneyOf(src)).Should().Be((30, 50));
    }

    [Theory]
    [InlineData(0L, "cancel:x")]
    [InlineData(-5L, "cancel:x")]
    [InlineData(10L, "")]
    [InlineData(10L, "cancel: x")]
    public void ABadReduction_IsRefused(long amount, string key)
    {
        var b = new Book();
        var order = b.Charge(100);
        Assert.Throws<ValidationException>(() => b.Reduce(order, amount, key));
        Assert.Throws<ValidationException>(() => b.Reduce(order, 10, new string('k', CreditReversals.MaxSourceKeyLength + 1)));
        b.Lines.Should().HaveCount(1);
    }

    // ---- §8.1 bounce -------------------------------------------------------------------------------------------------

    [Fact]
    public void ABounce_PutsTheDebtBackOnTheSameOrders_AndTakesItsOnAccountMoney()
    {
        // 100 paid: 30 on o1, 50 on o2, 20 on account; later 15 of that applied to o3. The bounce reverses 30, 50 and 15 on those
        // orders and takes the last 5 off the account. Another payment is untouched.
        var b = new Book();
        var (o1, o2, o3) = (b.Charge(30), b.Charge(80), b.Charge(40));
        var other = b.Pay(0, (o2, 30));
        var p = b.Pay(20, (o1, 30), (o2, 50));
        b.ApplyOnAccount(p, o3, 15);

        var postings = b.Bounce(p);

        postings.Select(x => (x.OrderId, x.ReceivableDeltaMinor, x.OnAccountDeltaMinor))
            .Should().Equal(((Guid?)o1, 30L, 0L), (o2, 50L, 0L), (o3, 15L, 0L), (null, 0L, -5L));
        postings.Should().OnlyContain(x => x.Kind == CreditLedgerKind.PaymentReversed && x.PaymentId == p && x.Reason == "test reason");
        (b.Of(o1).ReceivableMinor, b.Of(o2).ReceivableMinor, b.Of(o3).ReceivableMinor).Should().Be((30, 50, 40));
        (b.MoneyOf(p), b.MoneyOf(other)).Should().Be((0, 30));
        CreditReversals.Bounce(p, b.Lines, Info).Should().BeEmpty("everything is reversed: a second bounce has nothing to write");
    }

    [Fact]
    public void ABounce_AfterTheOrderWasCancelled_TakesTheMovedMoneyOffTheAccount()
    {
        var b = new Book();
        var order = b.Charge(100);
        var p = b.Pay(0, (order, 60));
        b.Reduce(order, null, $"cancel:{order:N}");   // 40 owed released, 60 on account in p's name

        b.Bounce(p).Select(x => (x.OrderId, x.ReceivableDeltaMinor, x.OnAccountDeltaMinor)).Should().Equal(((Guid?)null, 0L, -60L));
        b.Of(order).Should().Be(new CreditOrderBalance(100, 100, 0), "a cancelled order owes nothing again");
        (b.Company.CreditUsedMinor, b.Company.CreditOnAccountMinor, b.MoneyOf(p)).Should().Be((0, 0, 0));
    }

    // ---- property ----------------------------------------------------------------------------------------------------

    [Fact]
    public void RandomPaymentsReductionsAndBounces_KeepI4I5I6()
    {
        for (var seed = 1; seed <= 1_000; seed++)
        {
            var rnd = new Random(seed);
            var b = new Book();
            var orders = Enumerable.Range(0, 4).Select(_ => b.Charge(rnd.Next(1, 500))).ToList();
            var payments = new Dictionary<Guid, long>();
            var bounced = new HashSet<Guid>();
            for (var step = 0; step < 25; step++)
            {
                switch (rnd.Next(4))
                {
                    case 0:
                        var open = orders.Select(o => new CreditOpenCharge(o, b.Of(o).ReceivableMinor, Today, Now)).Where(c => c.ReceivableMinor > 0);
                        var amount = rnd.Next(1, 400);
                        var plan = CreditAllocationPlanner.Plan(amount, open);
                        payments[b.Pay(plan.OnAccountMinor, plan.Allocations.Select(x => (x.OrderId, x.AmountMinor)).ToArray())] = amount;
                        break;
                    case 1:
                        var source = payments.Keys.Where(p => !bounced.Contains(p))
                            .Select(p => (p, Left: b.Lines.Where(l => l.PaymentId == p).Sum(l => l.OnAccountDeltaMinor))).FirstOrDefault(s => s.Left > 0);
                        var owing = orders.FirstOrDefault(o => b.Of(o).ReceivableMinor > 0);
                        if (source.Left > 0 && owing != Guid.Empty) b.ApplyOnAccount(source.p, owing, Math.Min(source.Left, b.Of(owing).ReceivableMinor));
                        break;
                    case 2:
                        var order = orders[rnd.Next(orders.Count)];
                        long? asked = rnd.Next(3) == 0 ? null : rnd.Next(1, 300);
                        try { b.Reduce(order, asked, $"k{step}"); }
                        catch (ConflictException ex) when (ex.Code == "CREDIT_REDUCTION_EXCEEDS_CHARGE") { }
                        break;
                    default:
                        var victim = payments.Keys.Where(p => !bounced.Contains(p)).OrderBy(_ => rnd.Next()).FirstOrDefault();
                        if (victim != Guid.Empty) { b.Bounce(victim); bounced.Add(victim); }
                        break;
                }

                foreach (var o in orders)
                {
                    var x = b.Of(o);
                    (x.ReceivableMinor >= 0 && x.PaidMinor >= 0 && x.ReducedMinor <= x.ChargeMinor).Should().BeTrue($"seed {seed}: I4/I6 on {o}");
                }
                foreach (var (p, amount) in payments)
                    b.MoneyOf(p).Should().Be(bounced.Contains(p) ? 0 : amount, $"seed {seed}: I5 for {p}");
                b.Company.CreditUsedMinor.Should().Be(orders.Sum(o => b.Of(o).ReceivableMinor), $"seed {seed}: I1");
                b.Company.CreditOnAccountMinor.Should().Be(b.Lines.Sum(l => l.OnAccountDeltaMinor), $"seed {seed}: I2");
            }
        }
    }
}
