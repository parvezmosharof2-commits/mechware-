using FluentAssertions;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;

namespace Mechware.UnitTests.Credit;

/// <summary>
/// ADR-0010 §6 "apply on-account" (B3.5b, threat model CS9), without a database: on-account money per source, the oldest source
/// first, one <c>on_account_applied</c> line per (order, source), never more than is held or owed, and together with payments,
/// reductions and bounces it never breaks I1, I2, I4 or I5.
/// </summary>
public class CreditOnAccountTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static readonly CreditEventInfo Info = new(Today, Guid.NewGuid(), Guid.NewGuid(), "corr-unit", CreditOnAccount.Reason);
    private const string Request = "0123456789abcdef01234567:0123456789abcdef";

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

        /// <summary>A charge confirmed <paramref name="daysAgo"/> days ago, so older orders are due earlier.</summary>
        public Guid Charge(long amount, int daysAgo = 0)
        {
            var order = Guid.NewGuid();
            Lines.Add(_ledger.Charge(order, amount, Now.AddDays(-daysAgo), Now.AddDays(-daysAgo)).Entry);
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

        public List<CreditOpenCharge> Open() => Lines.Where(l => l.Kind == CreditLedgerKind.Charge)
            .Select(l => new CreditOpenCharge(l.OrderId!.Value, Of(l.OrderId.Value).ReceivableMinor, l.DueOn!.Value, l.CreatedAtUtc))
            .Where(c => c.ReceivableMinor > 0).ToList();

        public IReadOnlyList<CreditPosting> Apply(long? amount = null, IReadOnlyList<CreditAllocationRequest>? requests = null, string request = Request)
        {
            var postings = CreditOnAccount.Apply(CreditOnAccount.Sources(Lines), Open(), amount, requests, request, Info);
            foreach (var p in postings) Post(p);
            return postings;
        }

        public void Reduce(Guid order, string key)
        {
            foreach (var p in CreditReversals.Reduce(order, Of(order), Lines, null, key, null, Info).Postings) Post(p);
        }

        public void Bounce(Guid payment)
        {
            foreach (var p in CreditReversals.Bounce(payment, Lines, Info)) Post(p);
        }

        public CreditOrderBalance Of(Guid order) => _ledger.BalanceOf(order);

        public long OnAccountOf(Guid source) => Lines.Where(l => l.PaymentId == source || l.CreditNoteId == source).Sum(l => l.OnAccountDeltaMinor);

        /// <summary>What a payment's money is now: paid on orders + still on account (I5).</summary>
        public long MoneyOf(Guid payment) => Lines.Where(l => l.PaymentId == payment)
            .Sum(l => (l.OrderId is null ? 0 : l.Kind == CreditLedgerKind.ChargeReduced ? -l.OnAccountDeltaMinor : -l.ReceivableDeltaMinor) + l.OnAccountDeltaMinor);
    }

    private static readonly string[] Refusals =
        ["CREDIT_NOTHING_TO_APPLY", "CREDIT_ON_ACCOUNT_INSUFFICIENT", "CREDIT_ON_ACCOUNT_EXCEEDS_OPEN", "ALLOCATION_EXCEEDS_OUTSTANDING"];

    private static string CodeOf(Action act) => act.Should().Throw<ConflictException>().Which.Code;

    [Fact]
    public void Sources_AreTheMoneyStillHeldPerSource_OldestFirst()
    {
        // p1 (oldest) brought 30 on account and 10 of it was applied; p2 brought 20; p3's 15 was bounced away; a cancelled order
        // put 25 on account in the name of p4, which paid it (p4 counts from then: oldest MONEY first). A credit note's on-account
        // money (a correction in its name, B3.6) is its own source.
        var b = new Book();
        var o = b.Charge(100);
        var cancelled = b.Charge(25);
        var p4 = b.Pay(0, (cancelled, 25));
        var p1 = b.Pay(30);
        var p2 = b.Pay(20);
        var p3 = b.Pay(15);
        b.Bounce(p3);
        b.Reduce(cancelled, "cancel:c1");
        var note = Guid.NewGuid();
        b.Post(new CreditPosting(CreditLedgerKind.OnAccountCorrection, 0, 5, "adjust:test:note", Today, AdjustmentId: Guid.NewGuid(), CreditNoteId: note));
        b.Post(new CreditPosting(CreditLedgerKind.OnAccountApplied, -10, -10, "apply:test:1", Today, o, p1));

        CreditOnAccount.Sources(b.Lines).Select(s => (s.PaymentId ?? s.CreditNoteId, s.OnAccountMinor))
            .Should().Equal(((Guid?)p1, 20L), (p2, 20L), (p4, 25L), (note, 5L));
        CreditOnAccount.Sources(b.Lines).Sum(s => s.OnAccountMinor).Should().Be(b.Company.CreditOnAccountMinor);
    }

    [Fact]
    public void Apply_TakesTheOldestSourceFirst_OneLinePerOrderAndSource()
    {
        // Sources: p1 40 (oldest), p2 50. Orders: due earlier 30, due later 50. Applying 70 FIFO: the early order takes 30 of p1,
        // the later one the last 10 of p1 and 30 of p2.
        var b = new Book();
        var p1 = b.Pay(40);
        var p2 = b.Pay(50);
        var later = b.Charge(50, daysAgo: 1);
        var early = b.Charge(30, daysAgo: 20);

        var postings = b.Apply(70);

        postings.Select(p => (p.OrderId, p.PaymentId, p.ReceivableDeltaMinor, p.OnAccountDeltaMinor))
            .Should().Equal(((Guid?)early, (Guid?)p1, -30L, -30L), (later, p1, -10L, -10L), (later, p2, -30L, -30L));
        postings.Should().OnlyContain(p => p.Kind == CreditLedgerKind.OnAccountApplied && p.EventId == Info.EventId && p.ActorUserId == Info.ActorUserId
            && p.CorrelationId == "corr-unit" && p.Reason == CreditOnAccount.Reason && p.OccurredOn == Today);
        postings[0].IdempotencyKey.Should().Be($"apply:{Request}:{early:N}:{p1:N}").And.HaveLength(113);
        (b.OnAccountOf(p1), b.OnAccountOf(p2), b.Of(early).ReceivableMinor, b.Of(later).ReceivableMinor).Should().Be((0, 20, 0, 10));
        (b.Company.CreditUsedMinor, b.Company.CreditOnAccountMinor).Should().Be((10, 20));
        (b.MoneyOf(p1), b.MoneyOf(p2)).Should().Be((40, 50), "applying moves money, it never creates or loses any (I5)");
    }

    [Fact]
    public void Apply_WithoutAnAmount_AppliesAllThatFits_AndExplicitOrdersGoFirst()
    {
        var b = new Book();
        var p = b.Pay(100);
        var (old, mid, young) = (b.Charge(30, 30), b.Charge(40, 20), b.Charge(50, 10));

        // Named orders get exactly their amounts; with no amount that is all.
        b.Apply(requests: [new(young, 25)]).Select(x => (x.OrderId, x.ReceivableDeltaMinor)).Should().Equal(((Guid?)young, -25L));
        // An amount: the named order first, then the oldest due.
        b.Apply(20, [new(mid, 5)], "r2").Select(x => (x.OrderId, x.ReceivableDeltaMinor)).Should().Equal(((Guid?)mid, -5L), (old, -15L));
        // No amount, no orders: everything held or everything owed, whichever is less (55 held, 100 owed).
        b.Apply(request: "r3").Select(x => (x.OrderId, x.ReceivableDeltaMinor)).Should().Equal(((Guid?)old, -15L), (mid, -35L), (young, -5L));
        (b.Company.CreditOnAccountMinor, b.Company.CreditUsedMinor, b.MoneyOf(p)).Should().Be((0, 20, 100));
    }

    [Fact]
    public void Apply_MoreThanIsHeldOrOwed_OrNothing_IsRefused()
    {
        var b = new Book();
        var order = b.Charge(50);
        CodeOf(() => b.Apply()).Should().Be("CREDIT_NOTHING_TO_APPLY", "no money on account");
        b.Pay(80);   // FIFO would have paid the order; this payment is all on account (as if the order came later)
        CodeOf(() => b.Apply(81)).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");
        CodeOf(() => b.Apply(requests: [new(order, 81)])).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");
        CodeOf(() => b.Apply(60)).Should().Be("CREDIT_ON_ACCOUNT_EXCEEDS_OPEN", "the open orders owe only 50: never clamped");
        CodeOf(() => b.Apply(requests: [new(order, 51)])).Should().Be("ALLOCATION_EXCEEDS_OUTSTANDING");
        CodeOf(() => b.Apply(requests: [new(Guid.NewGuid(), 10)])).Should().Be("ALLOCATION_ORDER_NOT_OPEN");
        b.Invoking(x => x.Apply(0)).Should().Throw<ValidationException>();
        b.Invoking(x => x.Apply(-5)).Should().Throw<ValidationException>();
        b.Lines.Should().NotContain(l => l.Kind == CreditLedgerKind.OnAccountApplied, "a refusal posts nothing");

        b.Apply(50).Should().ContainSingle();
        CodeOf(() => b.Apply(request: "r2")).Should().Be("CREDIT_NOTHING_TO_APPLY", "nothing is owed any more");
    }

    [Fact]
    public void RandomApplications_WithPaymentsReductionsAndBounces_KeepI1I2I4I5_AndNoSourceGoesNegative()
    {
        for (var seed = 1; seed <= 1_000; seed++)
        {
            var rnd = new Random(seed);
            var b = new Book();
            var orders = new List<Guid>();
            var payments = new Dictionary<Guid, long>();
            var bounced = new HashSet<Guid>();
            for (var step = 0; step < 30; step++)
            {
                switch (rnd.Next(5))
                {
                    case 0:
                        orders.Add(b.Charge(rnd.Next(1, 400), rnd.Next(0, 40)));
                        break;
                    case 1:
                        var amount = rnd.Next(1, 300);
                        var plan = rnd.Next(2) == 0 ? new CreditAllocationPlan([], amount) : CreditAllocationPlanner.Plan(amount, b.Open());
                        payments[b.Pay(plan.OnAccountMinor, plan.Allocations.Select(x => (x.OrderId, x.AmountMinor)).ToArray())] = amount;
                        break;
                    case 2:
                        long? asked = rnd.Next(3) == 0 ? null : rnd.Next(1, 200);
                        var open = b.Open();
                        List<CreditAllocationRequest>? named = open.Count > 0 && rnd.Next(3) == 0 ? [new(open[rnd.Next(open.Count)].OrderId, rnd.Next(1, 50))] : null;
                        try { b.Apply(asked, named, $"r{step}"); }
                        catch (ConflictException ex) when (Refusals.Contains(ex.Code)) { }
                        catch (ValidationException) when (named is not null) { }   // a named amount above the asked amount
                        break;
                    case 3:
                        if (orders.Count > 0) { var o = orders[rnd.Next(orders.Count)]; b.Reduce(o, $"cancel:{o:N}"); }
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
                var sources = CreditOnAccount.Sources(b.Lines);   // throws if a source went negative
                sources.Sum(s => s.OnAccountMinor).Should().Be(b.Company.CreditOnAccountMinor, $"seed {seed}: I2 per source");
                b.Company.CreditUsedMinor.Should().Be(orders.Sum(o => b.Of(o).ReceivableMinor), $"seed {seed}: I1");
            }
        }
    }
}
