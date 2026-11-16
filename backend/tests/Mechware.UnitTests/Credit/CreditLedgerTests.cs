using FluentAssertions;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;

namespace Mechware.UnitTests.Credit;

/// <summary>ADR-0010 §1-§3 and threat model §১২ (CS8, CS14, CS18, CS26): the credit ledger's rules, without a database.</summary>
public class CreditLedgerTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 11, 16);

    private static Company NewCompany(long limitTaka = 1_000_000, CreditTerms terms = CreditTerms.Net30)
    {
        var c = Company.Register(Guid.NewGuid(), "Square Textiles Ltd", "TL-2020-1234", "0012345678901", null, null);
        c.Activate();
        c.SetCredit(terms, new Money(limitTaka, Currency.BDT));
        return c;
    }

    private static CreditPosting Line(CreditLedgerKind kind, long r, long a, Guid? order = null, Guid? payment = null,
        Guid? adjustment = null, Guid? note = null, string? key = null, DateOnly? dueOn = null) =>
        new(kind, r, a, key ?? $"{kind}:{Guid.NewGuid():N}", Today, order, payment, adjustment, note, dueOn);

    [Fact]
    public void DueOn_IsFixedAtTheCharge_AndATermsChangeDoesNotMoveIt()
    {
        var c = NewCompany(terms: CreditTerms.Net30);
        var ledger = new CreditLedger(c, []);
        var charge = ledger.Charge(Guid.NewGuid(), 50_000, Now, Now).Entry;

        charge.DueOn.Should().Be(Today.AddDays(30), "Net-30 from the confirmation date (owner decision 1 default)");
        charge.OccurredOn.Should().Be(Today);
        c.SetCredit(CreditTerms.Net15, new Money(1_000_000, Currency.BDT));
        charge.DueOn.Should().Be(Today.AddDays(30), "a later terms change never moves a due date");
        ledger.Charge(Guid.NewGuid(), 50_000, Now, Now).Entry.DueOn.Should().Be(Today.AddDays(15), "new charges use the terms of their day");
    }

    [Theory]
    [InlineData("2026-11-16T17:59:59Z", "2026-11-16")]   // 23:59:59 in Dhaka
    [InlineData("2026-11-16T18:00:00Z", "2026-11-17")]   // midnight in Dhaka: already the next day, still the 16th in UTC
    [InlineData("2026-12-31T18:30:00Z", "2027-01-01")]   // a new year in Dhaka first
    public void DueOn_UsesTheDhakaDate_AroundMidnight(string confirmedUtc, string dhakaDate)
    {
        var at = DateTime.Parse(confirmedUtc, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var day = DateOnly.Parse(dhakaDate);

        var charge = new CreditLedger(NewCompany(terms: CreditTerms.Net15), []).Charge(Guid.NewGuid(), 1, at, at).Entry;

        charge.OccurredOn.Should().Be(day);
        charge.DueOn.Should().Be(day.AddDays(15));
    }

    [Fact]
    public void ACharge_IsNumbered_MovesTheCachedTotal_AndAReplayIsANoOp()
    {
        var c = NewCompany();
        var order = Guid.NewGuid();
        var first = new CreditLedger(c, []).Charge(order, 70_000, Now, Now);

        first.AlreadyApplied.Should().BeFalse();
        first.Entry.Should().BeEquivalentTo(new
        {
            Seq = 1L,
            Kind = CreditLedgerKind.Charge,
            ReceivableDeltaMinor = 70_000L,
            ReceivableAfterMinor = 70_000L,
            OnAccountAfterMinor = 0L,
            IdempotencyKey = $"charge:{order:N}",
            CompanyId = c.Id,
            OrderId = (Guid?)order
        });
        (c.CreditUsedMinor, c.CreditLedgerSeq).Should().Be((70_000, 1));

        // The approval sweep runs twice, or a second request confirms the same order: the stored line is found, nothing moves.
        var again = new CreditLedger(c, [first.Entry]).Charge(order, 70_000, Now.AddMinutes(1), Now.AddMinutes(1));
        again.AlreadyApplied.Should().BeTrue();
        again.Entry.Should().BeSameAs(first.Entry);
        (c.CreditUsedMinor, c.CreditLedgerSeq).Should().Be((70_000, 1));

        new CreditLedger(c, [first.Entry]).Invoking(l => l.Charge(order, 70_001, Now, Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_LEDGER_KEY_REUSED");
    }

    [Fact]
    public void OnAccountMoney_DoesNotRaiseAvailableCredit()
    {
        var c = NewCompany(limitTaka: 1_000);
        var ledger = new CreditLedger(c, []);
        var order = Guid.NewGuid();
        var payment = Guid.NewGuid();
        ledger.Charge(order, 60_000, Now, Now);
        ledger.Post(Line(CreditLedgerKind.PaymentApplied, -60_000, 0, order, payment), Now);
        ledger.Post(Line(CreditLedgerKind.PaymentOnAccount, 0, 25_000, payment: payment), Now);

        (c.CreditUsedMinor, c.CreditOnAccountMinor).Should().Be((0, 25_000));
        c.CreditAvailable.Should().Be(new Money(1_000, Currency.BDT), "money held on account is not credit until staff apply it to an order");
    }

    [Fact]
    public void AReduction_TakesTheReceivableFirst_AndOnlyWhatWasPaidGoesOnAccount()
    {
        // ADR-0010 §8.2: charge 100, credit note 20, paid 30, receivable 50; cancelling reduces by 80 = 50 owed + 30 on account.
        var c = NewCompany();
        var ledger = new CreditLedger(c, []);
        var order = Guid.NewGuid();
        var payment = Guid.NewGuid();
        var note = Guid.NewGuid();
        ledger.Charge(order, 10_000, Now, Now);
        ledger.Post(Line(CreditLedgerKind.ChargeReduced, -2_000, 0, order, note: note), Now);
        ledger.Post(Line(CreditLedgerKind.PaymentApplied, -3_000, 0, order, payment), Now);
        ledger.BalanceOf(order).Should().Be(new CreditOrderBalance(10_000, 2_000, 5_000));

        ledger.Invoking(l => l.Post(Line(CreditLedgerKind.ChargeReduced, -5_001, 0, order), Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_EXCEEDS_RECEIVABLE");
        ledger.Post(Line(CreditLedgerKind.ChargeReduced, -5_000, 0, order), Now);
        ledger.Invoking(l => l.Post(Line(CreditLedgerKind.ChargeReduced, 0, 3_001, order, payment), Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_EXCEEDS_PAID");
        ledger.Post(Line(CreditLedgerKind.ChargeReduced, 0, 3_000, order, payment), Now);

        ledger.BalanceOf(order).Should().Be(new CreditOrderBalance(10_000, 10_000, 0), "reductions never exceed the charge (I6)");
        (c.CreditUsedMinor, c.CreditOnAccountMinor).Should().Be((0, 3_000));
    }

    public static TheoryData<CreditLedgerKind, long, long, bool, bool, bool, bool> BadLines => new()
    {
        // kind, R, A, order, payment, adjustment, credit note
        { CreditLedgerKind.PaymentApplied, 5_000, 0, true, true, false, false },        // a payment never raises a debt
        { CreditLedgerKind.WriteOff, 5_000, 0, true, false, true, false },              // an adjustment never raises a debt
        { CreditLedgerKind.PaymentReversed, 5_000, 0, true, true, true, false },        // ... not even through a reversal
        { CreditLedgerKind.PaymentReversed, 5_000, 0, false, true, false, false },      // a debt is always an order's
        { CreditLedgerKind.PaymentOnAccount, 0, 5_000, false, false, false, false },    // on-account money names its source
        { CreditLedgerKind.OnAccountApplied, -5_000, -4_000, true, true, false, false }, // both sides move by the same amount
        { CreditLedgerKind.OnAccountCorrection, 0, 0, false, true, true, false },       // a line moves something
        { CreditLedgerKind.OnAccountPayout, 0, 5_000, false, true, true, false },       // a payout only takes money out
        { (CreditLedgerKind)0, -1, 0, true, true, false, false },                       // unknown kind
    };

    [Theory]
    [MemberData(nameof(BadLines))]
    public void EveryKind_RefusesTheWrongSignsOrReferences(CreditLedgerKind kind, long r, long a, bool order, bool payment, bool adjustment, bool note)
    {
        var p = Line(kind, r, a, order ? Guid.NewGuid() : null, payment ? Guid.NewGuid() : null, adjustment ? Guid.NewGuid() : null, note ? Guid.NewGuid() : null);
        FluentActions.Invoking(() => CreditLedgerEntry.Validate(p)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => CreditLedgerEntry.Validate(p with { Kind = CreditLedgerKind.Charge, DueOn = Today, ReceivableDeltaMinor = -1 }))
            .Should().Throw<ValidationException>("a charge is positive");
        FluentActions.Invoking(() => CreditLedgerEntry.Validate(Line(CreditLedgerKind.PaymentOnAccount, 0, 1, payment: Guid.NewGuid(), dueOn: Today)))
            .Should().Throw<ValidationException>("only a charge has a due date");
    }

    [Fact]
    public void NoOrderReceivable_EverGoesNegative()
    {
        // CS8 (property): whatever is thrown at an order (payments, reductions, write-offs, reversals of any size), every line
        // that would take it below 0 or above its charge is refused, and the refusal changes nothing.
        for (var seed = 0; seed < 2_000; seed++)
        {
            var rng = new Random(seed);
            var c = NewCompany();
            var ledger = new CreditLedger(c, []);
            var order = Guid.NewGuid();
            var payment = Guid.NewGuid();
            var charge = rng.Next(1, 100_000);
            ledger.Charge(order, charge, Now, Now);
            for (var i = 0; i < 30; i++)
            {
                var x = rng.Next(1, charge + 2);
                var line = rng.Next(4) switch
                {
                    0 => Line(CreditLedgerKind.PaymentApplied, -x, 0, order, payment),
                    1 => Line(CreditLedgerKind.ChargeReduced, -x, 0, order),
                    2 => Line(CreditLedgerKind.WriteOff, -x, 0, order, adjustment: Guid.NewGuid()),
                    _ => Line(CreditLedgerKind.PaymentReversed, x, 0, order, payment)
                };
                var before = (ledger.BalanceOf(order), c.CreditUsedMinor, c.CreditLedgerSeq);
                try { ledger.Post(line, Now); }
                catch (ConflictException) { (ledger.BalanceOf(order), c.CreditUsedMinor, c.CreditLedgerSeq).Should().Be(before, $"seed {seed}"); }

                var b = ledger.BalanceOf(order);
                b.ReceivableMinor.Should().BeInRange(0, b.ChargeMinor, $"seed {seed} step {i}");
                b.PaidMinor.Should().BeGreaterThanOrEqualTo(0, $"seed {seed} step {i}");
                c.CreditUsedMinor.Should().Be(b.ReceivableMinor);
            }
        }
    }

    [Fact]
    public void RandomLedgerOperations_KeepEveryInvariant_AndAReplayedKeyChangesNothing()
    {
        // Property, 2,000 seeded rounds of random operations of every kind, valid and invalid. After each step: the cached
        // totals are the sums of the lines (I1, I2), lines are numbered 1..n and each running total is the previous one plus
        // its delta (I3), every order stays 0 ≤ receivable ≤ charge with reductions ≤ charge (I4, I6), nothing is negative,
        // a refused operation changes nothing, and replaying an earlier key returns that line and changes nothing.
        for (var seed = 0; seed < 2_000; seed++)
        {
            var rng = new Random(seed);
            var c = NewCompany(limitTaka: rng.Next(1, 20_000));
            var ledger = new CreditLedger(c, []);
            var lines = new List<CreditLedgerEntry>();
            var postings = new List<CreditPosting>();
            var orders = new List<Guid>();
            var sources = new List<Guid> { Guid.NewGuid() };

            for (var step = 0; step < 40; step++)
            {
                var order = orders.Count > 0 ? orders[rng.Next(orders.Count)] : Guid.NewGuid();
                var source = sources[rng.Next(sources.Count)];
                var x = (long)rng.Next(1, 600_000);
                var before = (c.CreditUsedMinor, c.CreditOnAccountMinor, c.CreditLedgerSeq);
                try
                {
                    CreditPostResult result;
                    var op = rng.Next(11);
                    if (op == 0)
                    {
                        var id = Guid.NewGuid();
                        result = ledger.Charge(id, x, Now, Now);
                        orders.Add(id);
                    }
                    else if (op == 10 && postings.Count > 0)
                    {
                        var replay = postings[rng.Next(postings.Count)];
                        result = replay.Kind == CreditLedgerKind.Charge
                            ? ledger.Charge(replay.OrderId!.Value, replay.ReceivableDeltaMinor, Now, Now)
                            : ledger.Post(replay, Now);
                        result.AlreadyApplied.Should().BeTrue($"seed {seed}: a replayed key is a no-op");
                        (c.CreditUsedMinor, c.CreditOnAccountMinor, c.CreditLedgerSeq).Should().Be(before);
                        continue;
                    }
                    else
                    {
                        var p = op switch
                        {
                            1 => Line(CreditLedgerKind.PaymentApplied, -x, 0, order, source),
                            2 => Line(CreditLedgerKind.PaymentOnAccount, 0, x, payment: source),
                            3 => Line(CreditLedgerKind.OnAccountApplied, -x, -x, order, source),
                            4 => rng.Next(2) == 0 ? Line(CreditLedgerKind.PaymentReversed, x, 0, order, source) : Line(CreditLedgerKind.PaymentReversed, 0, -x, payment: source),
                            5 => rng.Next(2) == 0 ? Line(CreditLedgerKind.ChargeReduced, -x, 0, order) : Line(CreditLedgerKind.ChargeReduced, 0, x, order, source),
                            6 => Line(CreditLedgerKind.WriteOff, -x, 0, order, adjustment: Guid.NewGuid()),
                            7 => Line(CreditLedgerKind.OnAccountCorrection, 0, rng.Next(2) == 0 ? x : -x, payment: source, adjustment: Guid.NewGuid()),
                            8 => Line(CreditLedgerKind.OnAccountPayout, 0, -x, payment: source, adjustment: Guid.NewGuid()),
                            _ => Line(CreditLedgerKind.WriteOff, x, 0, order, adjustment: Guid.NewGuid())   // an adjustment raising a debt: always refused
                        };
                        result = ledger.Post(p, Now);
                        postings.Add(p);
                        if (rng.Next(3) == 0) sources.Add(Guid.NewGuid());
                    }
                    if (result.Entry.Kind == CreditLedgerKind.Charge) postings.Add(Line(CreditLedgerKind.Charge, x, 0, result.Entry.OrderId, dueOn: result.Entry.DueOn));
                    lines.Add(result.Entry);
                }
                catch (DomainException)
                {
                    (c.CreditUsedMinor, c.CreditOnAccountMinor, c.CreditLedgerSeq).Should().Be(before, $"seed {seed} step {step}: a refusal changes nothing");
                }

                lines.Sum(l => l.ReceivableDeltaMinor).Should().Be(c.CreditUsedMinor, $"seed {seed} step {step} (I1)");
                lines.Sum(l => l.OnAccountDeltaMinor).Should().Be(c.CreditOnAccountMinor, $"seed {seed} step {step} (I2)");
                c.CreditLedgerSeq.Should().Be(lines.Count);
                (c.CreditUsedMinor >= 0 && c.CreditOnAccountMinor >= 0).Should().BeTrue();
                for (var i = 0; i < lines.Count; i++)
                {
                    lines[i].Seq.Should().Be(i + 1, $"seed {seed} (I3)");
                    lines[i].ReceivableAfterMinor.Should().Be((i == 0 ? 0 : lines[i - 1].ReceivableAfterMinor) + lines[i].ReceivableDeltaMinor);
                    lines[i].OnAccountAfterMinor.Should().Be((i == 0 ? 0 : lines[i - 1].OnAccountAfterMinor) + lines[i].OnAccountDeltaMinor);
                }
                foreach (var o in orders)
                {
                    var b = ledger.BalanceOf(o);
                    b.ReceivableMinor.Should().BeInRange(0, b.ChargeMinor, $"seed {seed} step {step} (I4)");
                    b.ReducedMinor.Should().BeInRange(0, b.ChargeMinor, $"seed {seed} step {step} (I6)");
                    b.PaidMinor.Should().BeGreaterThanOrEqualTo(0);
                }
                lines.Where(l => l.ReceivableDeltaMinor > 0).Should().OnlyContain(l => l.Kind == CreditLedgerKind.Charge || l.Kind == CreditLedgerKind.PaymentReversed,
                    "only a charge or a reversed payment raises what a company owes");
            }
        }
    }
}
