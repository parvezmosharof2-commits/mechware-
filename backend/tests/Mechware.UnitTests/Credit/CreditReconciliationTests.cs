using FluentAssertions;
using Mechware.Application.Credit;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Infrastructure;
using Mechware.Infrastructure.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using R = Mechware.Domain.Credit.CreditReconciliation;

namespace Mechware.UnitTests.Credit;

/// <summary>
/// ADR-0010 §11 (B3.10, threat model CS28), without a database: books written only through the real ledger rules never give a finding
/// (property), and each kind of damage a bug or a hand edit could do gives exactly its own finding.
/// </summary>
public class CreditReconciliationTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 20, 30, 0, DateTimeKind.Utc);   // 02:30 Dhaka, 17 Nov
    private static readonly DateOnly Today = CreditCalendar.DhakaDate(Now);
    private static readonly Guid Recorder = Guid.NewGuid(), Verifier = Guid.NewGuid(), Requester = Guid.NewGuid(), Approver = Guid.NewGuid();
    private static readonly CreditPaymentRules Rules = new CreditOptions().PaymentRules();
    private static readonly CreditHoldExpectation Hold = Expect(new CreditOptions());
    private static readonly CreditHoldExpectation NoHold = Expect(new CreditOptions { HoldAfterDaysPastDue = 0 });

    private static CreditHoldExpectation Expect(CreditOptions o) =>
        new(o.HoldDueBefore(Now), o.HoldDueBefore(Now.AddHours(-1)), Now.AddHours(-1), Now.AddDays(o.HoldOverrideMaxDays).AddHours(1));

    private static CreditEventInfo On(string reason = "test") => new(Today, Guid.NewGuid(), Approver, "corr-recon", reason);

    /// <summary>A company's books written through the domain rules only, as the services write them.</summary>
    private sealed class Books
    {
        public readonly Company Company;
        public readonly List<CreditLedgerEntry> Lines = [];
        public readonly List<CreditBookOrder> Orders = [];
        public readonly List<CreditPayment> Payments = [];
        public readonly List<CreditAdjustment> Adjustments = [];
        public DateTime? HoldSince, OverrideUntil;
        private readonly CreditLedger _ledger;

        public Books()
        {
            Company = Company.Register(Guid.NewGuid(), "Reconciled Traders Ltd", "TL-2020-9999", "0012345678903", null, null);
            Company.Activate();
            Company.SetCredit(CreditTerms.Net30, new Money(100_000_000, Currency.BDT));
            _ledger = new CreditLedger(Company, []);
        }

        public Guid Charge(long amount, int daysAgo = 0)
        {
            var order = Guid.NewGuid();
            var at = Now.AddDays(-daysAgo);
            Lines.Add(_ledger.Charge(order, amount, at, at).Entry);
            Orders.Add(new CreditBookOrder(order, Company.Id, true, true, false, amount));
            return order;
        }

        public void Post(CreditPosting p, DateTime? at = null)
        {
            var r = _ledger.Post(p, at ?? Now);
            if (!r.AlreadyApplied) Lines.Add(r.Entry);
        }

        public CreditPayment Record(long amount, CreditPaymentMethod method = CreditPaymentMethod.Rtgs)
        {
            var payment = CreditPayment.Record(Guid.NewGuid(), new CreditPaymentInput(Company.Id, method, amount,
                method == CreditPaymentMethod.Cash ? null : "TRX" + Guid.NewGuid().ToString("N")[..12], null, Today), Recorder,
                "key-" + Guid.NewGuid().ToString("N"), new string('a', 64), Rules, Now);
            Payments.Add(payment);
            return payment;
        }

        /// <summary>Verified now; the planner's lines (named orders, then the oldest due), the rest on account.</summary>
        public CreditPayment Pay(long amount, CreditPaymentMethod method = CreditPaymentMethod.Rtgs, DateTime? at = null)
        {
            var payment = Record(amount, method);
            payment.Verify(Verifier, Today, Now);
            foreach (var p in CreditAllocationPlanner.Plan(amount, Open()).Postings(payment.Id, Today, Guid.NewGuid(), null, null)) Post(p, at);
            return payment;
        }

        public void Bounce(CreditPayment payment)
        {
            foreach (var p in CreditReversals.Bounce(payment.Id, Lines, On("returned"))) Post(p);
            payment.Bounce(Verifier, "returned by the bank", Now);
        }

        public void Cancel(Guid order)
        {
            foreach (var p in CreditReversals.Reduce(order, Balance(order), Lines, null, $"cancel:{order:N}", null, On("cancelled")).Postings) Post(p);
            var i = Orders.FindIndex(o => o.Id == order);
            Orders[i] = Orders[i] with { Cancelled = true };
        }

        public void CreditNote(Guid order, long amount)
        {
            var note = Guid.NewGuid();
            foreach (var p in CreditReversals.Reduce(order, Balance(order), Lines, amount, $"cn:{note:N}", note, On("credit note")).Postings) Post(p);
        }

        public void Apply() => CreditOnAccount.Apply(CreditOnAccount.Sources(Lines), Open(), null, null, Guid.NewGuid().ToString("N"), On(CreditOnAccount.Reason))
            .ToList().ForEach(p => Post(p));

        public CreditAdjustment Ask(CreditAdjustmentKind kind, long amount, Guid? order = null, Guid? payment = null, Guid? note = null)
        {
            var a = CreditAdjustment.Request(Guid.NewGuid(), new CreditAdjustmentInput(Company.Id, kind, amount, "agreed with the customer in writing",
                order, payment, note, kind == CreditAdjustmentKind.OnAccountPayout ? "MWOUT" + Guid.NewGuid().ToString("N")[..8] : null), Requester, Rules.MaxPaymentMinor, Now);
            Adjustments.Add(a);
            return a;
        }

        public CreditAdjustment Adjust(CreditAdjustmentKind kind, long amount, Guid? order = null, CreditPayment? payment = null, Guid? note = null)
        {
            var a = Ask(kind, amount, order, payment?.Id, note);
            var posting = a.Posting(Lines, payment, 0, On("approved"));
            a.Approve(Approver, Now, payment);
            Post(posting);
            return a;
        }

        public CreditOrderBalance Balance(Guid order) => new CreditLedger(Company, Lines).BalanceOf(order);

        public List<CreditOpenCharge> Open() => Lines.Where(l => l.Kind == CreditLedgerKind.Charge)
            .Select(l => new CreditOpenCharge(l.OrderId!.Value, Balance(l.OrderId.Value).ReceivableMinor, l.DueOn!.Value, l.CreatedAtUtc))
            .Where(c => c.ReceivableMinor > 0).ToList();

        public CreditBook Book() => new(
            new CreditBookCompany(Company.Id, Company.CreditUsedMinor, Company.CreditOnAccountMinor, Company.CreditLedgerSeq, HoldSince, OverrideUntil),
            Lines.Select(Line).ToList(), Orders.ToList(),
            Payments.Select(p => new CreditBookPayment(p.Id, p.CompanyId, p.Status, p.AmountMinor)).ToList(),
            Adjustments.Select(a => new CreditBookAdjustment(a.Id, a.CompanyId, a.Status, a.Kind, a.AmountMinor, a.OrderId, a.PaymentId, a.CreditNoteId)).ToList());
    }

    private static CreditBookLine Line(CreditLedgerEntry l) => new(l.Seq, l.Kind, l.OrderId, l.PaymentId, l.AdjustmentId, l.CreditNoteId,
        l.ReceivableDeltaMinor, l.OnAccountDeltaMinor, l.ReceivableAfterMinor, l.OnAccountAfterMinor, l.DueOn, l.CreatedAtUtc);

    private static IReadOnlyList<CreditReconciliationFinding> Check(CreditBook book, CreditHoldExpectation? hold = null) => R.Check(book, hold ?? Hold);

    private static List<string> Checks(CreditBook book, CreditHoldExpectation? hold = null) => Check(book, hold).Select(f => f.Check).ToList();

    /// <summary>Two orders, a cheque that paid one and left money on account, a cash payment and a write-off: a consistent book.</summary>
    private static (Books B, Guid Paid, Guid Open, CreditPayment Cheque, CreditPayment Cash) Sample()
    {
        var b = new Books();
        var paid = b.Charge(30_000, daysAgo: 3);
        var open = b.Charge(50_000, daysAgo: 1);
        var cheque = b.Pay(40_000, CreditPaymentMethod.Rtgs);     // a transfer that can bounce; pays 30,000 + 10,000 of the second
        var cash = b.Pay(5_000, CreditPaymentMethod.Cash);         // pays 5,000 more of the second
        b.Adjust(CreditAdjustmentKind.WriteOff, 1_000, open);
        return (b, paid, open, cheque, cash);
    }

    // ---- CS28: a consistent book ---------------------------------------------------------------------------------------------

    [Fact]
    public void Reconciliation_OnAConsistentBook_ReportsNothing()
    {
        var (b, _, _, _, _) = Sample();
        Check(b.Book()).Should().BeEmpty();
        Check(new Books().Book()).Should().BeEmpty("a company without credit has nothing to reconcile");
    }

    [Fact]
    public void RandomBooks_WrittenThroughTheLedgerRules_NeverGiveAFinding()
    {
        for (var seed = 1; seed <= 1_000; seed++)
        {
            var rnd = new Random(seed);
            var b = new Books();
            var orders = new List<Guid>();
            for (var step = 0; step < 30; step++)
            {
                var payments = b.Payments.Where(p => p.Status == CreditPaymentStatus.Verified).ToList();
                try
                {
                    switch (rnd.Next(10))
                    {
                        case 0 or 1: orders.Add(b.Charge(rnd.Next(1, 500), rnd.Next(0, 60))); break;
                        case 2: b.Pay(rnd.Next(1, 400), rnd.Next(3) == 0 ? CreditPaymentMethod.Cash : CreditPaymentMethod.Rtgs); break;
                        case 3:
                            var recorded = b.Record(rnd.Next(1, 400));
                            if (rnd.Next(2) == 0) recorded.Reject(Verifier, "not in the bank statement", Now);
                            break;
                        case 4 when payments.Count > 0:
                            var victim = payments[rnd.Next(payments.Count)];
                            if (victim.Method != CreditPaymentMethod.Cash) b.Bounce(victim);
                            break;
                        case 5 when orders.Count > 0: b.Cancel(orders[rnd.Next(orders.Count)]); break;
                        case 6 when orders.Count > 0: b.CreditNote(orders[rnd.Next(orders.Count)], rnd.Next(1, 200)); break;
                        case 7: b.Apply(); break;
                        case 8 when orders.Count > 0: b.Adjust(CreditAdjustmentKind.WriteOff, rnd.Next(1, 200), orders[rnd.Next(orders.Count)]); break;
                        case 9 when payments.Count > 0:
                            var source = payments[rnd.Next(payments.Count)];
                            var kind = rnd.Next(2) == 0 ? CreditAdjustmentKind.OnAccountPayout : CreditAdjustmentKind.OnAccountCorrection;
                            if (rnd.Next(4) == 0) b.Ask(kind, rnd.Next(1, 100), payment: source.Id);   // requested only: no line
                            else b.Adjust(kind, kind == CreditAdjustmentKind.OnAccountPayout ? rnd.Next(1, 100) : -rnd.Next(1, 100), payment: source);
                            break;
                    }
                }
                catch (ConflictException ex) when (ex.Code is "CREDIT_EXCEEDS_RECEIVABLE" or "CREDIT_ON_ACCOUNT_INSUFFICIENT" or "CREDIT_REDUCTION_EXCEEDS_CHARGE"
                    or "CREDIT_NOTHING_TO_APPLY" or "CREDIT_LIMIT_EXCEEDED" or "CREDIT_REDUCTION_CONFLICT")
                { }
                // Old charges are overdue; with no sweep here the hold notes are left out ("flag only" + no note).
                Check(b.Book(), NoHold).Should().BeEmpty($"seed {seed}, step {step}");
            }
        }
    }

    // ---- CS28: every kind of damage is found, and only it ---------------------------------------------------------------------

    [Fact]
    public void CachedTotalsOrALineCounter_ThatDifferFromTheLines_AreFound()
    {
        var b = Sample().B.Book();
        Check(b with { Company = b.Company with { CreditUsedMinor = b.Company.CreditUsedMinor + 1 } })
            .Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.CreditUsed, null, 34_000, 34_001));
        Checks(b with { Company = b.Company with { OnAccountMinor = 1 } }).Should().Equal(R.OnAccount);
        Checks(b with { Company = b.Company with { LedgerSeq = b.Company.LedgerSeq - 1 } }).Should().Equal(R.LedgerSeq);
    }

    [Fact]
    public void ABrokenChain_IsFound()
    {
        var b = Sample().B.Book();
        var lines = b.Lines.ToList();
        // A gap: line 3 renumbered 4 and everything after moved up (counter too).
        var gap = lines.Select(l => l.Seq >= 3 ? l with { Seq = l.Seq + 1 } : l).ToList();
        Check(b with { Lines = gap, Company = b.Company with { LedgerSeq = b.Company.LedgerSeq + 1 } })
            .Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.LedgerChain, null, 3, 4));
        // A wrong running total on line 2 only (its delta and the sums stay right).
        lines[1] = lines[1] with { ReceivableAfterMinor = lines[1].ReceivableAfterMinor + 1 };
        Checks(b with { Lines = lines }).Should().Equal(R.LedgerChain);
        lines = b.Lines.ToList();
        lines[1] = lines[1] with { OnAccountAfterMinor = lines[1].OnAccountAfterMinor + 1 };
        Checks(b with { Lines = lines }).Should().Equal(R.LedgerChain);
    }

    [Fact]
    public void AConfirmedCreditOrder_WithoutExactlyOneCharge_OrWithAnotherAmount_IsFound()
    {
        var (books, paid, open, _, _) = Sample();
        var b = books.Book();
        var never = new CreditBookOrder(Guid.NewGuid(), b.Company.Id, true, true, false, 7_000);
        Check(b with { Orders = [.. b.Orders, never] }).Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.OrderCharge, never.Id, 1, 0));
        // Not confirmed, or not on credit, or another company's: no charge expected.
        Check(b with
        {
            Orders = [.. b.Orders, never with { Confirmed = false }, never with { Id = Guid.NewGuid(), PaysOnCredit = false },
            never with { Id = Guid.NewGuid(), CompanyId = Guid.NewGuid() }]
        }).Should().BeEmpty();

        // A second charge of the same order (another key), the totals and chain kept right.
        var charge = b.Lines.Single(l => l.OrderId == open && l.Kind == CreditLedgerKind.Charge);
        var last = b.Lines[^1];
        var twice = b with
        {
            Lines = [.. b.Lines, charge with { Seq = last.Seq + 1, ReceivableAfterMinor = last.ReceivableAfterMinor + charge.ReceivableDeltaMinor, OnAccountAfterMinor = last.OnAccountAfterMinor }],
            Company = b.Company with { LedgerSeq = last.Seq + 1, CreditUsedMinor = b.Company.CreditUsedMinor + charge.ReceivableDeltaMinor }
        };
        Check(twice).Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.OrderCharge, open, 1, 2));

        var other = b.Orders.Select(o => o.Id == paid ? o with { GrandTotalMinor = 30_001 } : o).ToList();
        Check(b with { Orders = other }).Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.OrderChargeAmount, paid, 30_001, 30_000));
    }

    [Fact]
    public void AnOrphanLine_OfAnOrderWithoutACharge_IsFound()
    {
        var b = Sample().B.Book();
        var order = new CreditBookOrder(Guid.NewGuid(), b.Company.Id, true, false, false, 1_000);
        var payment = new CreditBookPayment(Guid.NewGuid(), b.Company.Id, CreditPaymentStatus.Verified, 500);
        var last = b.Lines[^1];
        var orphan = new CreditBookLine(last.Seq + 1, CreditLedgerKind.PaymentApplied, order.Id, payment.Id, null, null, -500, 0,
            last.ReceivableAfterMinor - 500, last.OnAccountAfterMinor, null, Now);
        var checks = Checks(b with
        {
            Lines = [.. b.Lines, orphan],
            Orders = [.. b.Orders, order],
            Payments = [.. b.Payments, payment],
            Company = b.Company with { LedgerSeq = orphan.Seq, CreditUsedMinor = orphan.ReceivableAfterMinor }
        });
        checks.Should().Contain(R.OrderCharge).And.Contain(R.OrderBalance, "it takes the order below zero");
    }

    [Fact]
    public void AnOrderPaidBelowZero_OrAPaidPartNotAttributed_IsFound()
    {
        var (books, paid, _, cheque, _) = Sample();
        var b = books.Book();
        var lines = b.Lines.ToList();
        // The cheque's line on the first order names no payment: the order is paid, but by nobody.
        var i = lines.FindIndex(l => l.OrderId == paid && l.Kind == CreditLedgerKind.PaymentApplied);
        lines[i] = lines[i] with { PaymentId = null };
        Check(b with { Lines = lines }).Should().ContainEquivalentOf(new CreditReconciliationFinding(b.Company.Id, R.OrderPaidAttribution, paid, 30_000, 0));

        // A second payment line takes the paid order below zero (the next lines keep the chain and the totals).
        var last = b.Lines[^1];
        var over = new CreditBookLine(last.Seq + 1, CreditLedgerKind.PaymentApplied, paid, cheque.Id, null, null, -1, 0, last.ReceivableAfterMinor - 1, last.OnAccountAfterMinor, null, Now);
        Checks(b with { Lines = [.. b.Lines, over], Company = b.Company with { LedgerSeq = over.Seq, CreditUsedMinor = over.ReceivableAfterMinor } })
            .Should().Contain(R.OrderBalance).And.Contain(R.PaymentVerifiedAmount);
    }

    [Fact]
    public void AnOrderOwingMoreThanItsCharge_IsFound()
    {
        // A reversal line puts back more than was ever paid on the open order: its receivable rises above its charge (paid below zero),
        // while receivable and reduced stay positive. The totals and the chain are kept right.
        var (books, _, open, cheque, _) = Sample();
        var b = books.Book();
        var last = b.Lines[^1];
        var line = new CreditBookLine(last.Seq + 1, CreditLedgerKind.PaymentReversed, open, cheque.Id, null, null, 20_000, 0,
            last.ReceivableAfterMinor + 20_000, last.OnAccountAfterMinor, null, Now);
        Check(b with { Lines = [.. b.Lines, line], Company = b.Company with { LedgerSeq = line.Seq, CreditUsedMinor = line.ReceivableAfterMinor } })
            .Should().ContainEquivalentOf(new CreditReconciliationFinding(b.Company.Id, R.OrderBalance, open, 0, -5_000));
    }

    [Fact]
    public void ACancelledOrder_ThatStillOwes_IsFound()
    {
        var (books, _, open, _, _) = Sample();
        var b = books.Book();
        Check(b with { Orders = b.Orders.Select(o => o.Id == open ? o with { Cancelled = true } : o).ToList() })
            .Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.CancelledOrderNotReleased, open, 50_000, 1_000));
        books.Cancel(open);
        Check(books.Book()).Should().BeEmpty("a cancellation releases what is owed and moves what was paid on account");
    }

    [Fact]
    public void ASourceSpendingMoreOnAccountThanItBrought_IsFound()
    {
        // P1 brings 100 on account, P2 100; then a payout takes 150 from P1. Totals, chain, payments and the adjustment all add up.
        var b = new Books();
        var (p1, p2) = (b.Pay(100), b.Pay(100));
        var payout = b.Ask(CreditAdjustmentKind.OnAccountPayout, 150, payment: p1.Id);
        payout.Approve(Approver, Now, p1);
        var book = b.Book();
        var last = book.Lines[^1];
        var line = new CreditBookLine(last.Seq + 1, CreditLedgerKind.OnAccountPayout, null, p1.Id, payout.Id, null, 0, -150, last.ReceivableAfterMinor, 50, null, Now);
        Check(book with { Lines = [.. book.Lines, line], Company = book.Company with { LedgerSeq = line.Seq, OnAccountMinor = 50 } })
            .Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.SourceOnAccountNegative, p1.Id, 0, -50));
        p2.Status.Should().Be(CreditPaymentStatus.Verified);
    }

    [Fact]
    public void AVerifiedPaymentWithoutItsLines_AndLinesOfAPaymentNotVerified_AreFound()
    {
        var (books, _, _, cheque, _) = Sample();
        var b = books.Book();
        var ghost = new CreditBookPayment(Guid.NewGuid(), b.Company.Id, CreditPaymentStatus.Verified, 9_000);
        Check(b with { Payments = [.. b.Payments, ghost] }).Select(f => (f.Check, f.SubjectId, f.Expected, f.Actual))
            .Should().BeEquivalentTo([(R.PaymentVerifiedAmount, (Guid?)ghost.Id, 9_000L, 0L), (R.PaymentBalance, (Guid?)ghost.Id, 9_000L, 0L)]);
        foreach (var status in new[] { CreditPaymentStatus.Recorded, CreditPaymentStatus.Rejected })
            Check(b with { Payments = b.Payments.Select(p => p.Id == cheque.Id ? p with { Status = status } : p).ToList() })
                .Should().ContainSingle(status.ToString()).Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.PaymentNotVerified, cheque.Id, 0, 2));
        books.Record(1_000);
        Check(books.Book()).Should().BeEmpty("a recorded payment has no line yet");
    }

    [Fact]
    public void ABouncedPayment_ThatStillPays_IsFound()
    {
        var (books, _, _, cheque, _) = Sample();
        var b = books.Book();
        Check(b with { Payments = b.Payments.Select(p => p.Id == cheque.Id ? p with { Status = CreditPaymentStatus.Bounced } : p).ToList() })
            .Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.PaymentBalance, cheque.Id, 0, 40_000));
        books.Bounce(cheque);
        Check(books.Book()).Should().BeEmpty();
        var bounced = books.Book();
        Check(bounced with { Payments = bounced.Payments.Select(p => p.Id == cheque.Id ? p with { Status = CreditPaymentStatus.Verified } : p).ToList() })
            .Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.PaymentBalance, cheque.Id, 40_000, 0));
        Check(bounced with { Payments = bounced.Payments.Select(p => p.Id == cheque.Id ? p with { AmountMinor = 41_000 } : p).ToList() })
            .Select(f => f.Check).Should().Equal(R.PaymentVerifiedAmount);
    }

    [Fact]
    public void AnApprovedAdjustmentWithoutItsLine_OrALineOfAnotherShape_OrOfAnUnapprovedOne_IsFound()
    {
        var (books, _, open, cheque, _) = Sample();
        var b = books.Book();
        var writeOff = b.Adjustments.Single();
        var approved = new CreditBookAdjustment(Guid.NewGuid(), b.Company.Id, CreditAdjustmentStatus.Approved, CreditAdjustmentKind.WriteOff, 500, open, null, null);
        Check(b with { Adjustments = [.. b.Adjustments, approved] }).Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.AdjustmentLine, approved.Id, 1, 0));
        Check(b with { Adjustments = [.. b.Adjustments, approved with { Status = CreditAdjustmentStatus.Requested }] }).Should().BeEmpty();
        foreach (var status in new[] { CreditAdjustmentStatus.Requested, CreditAdjustmentStatus.Rejected })
            Checks(b with { Adjustments = [writeOff with { Status = status }] }).Should().Equal([R.AdjustmentLine], status.ToString());
        Check(b with { Adjustments = [writeOff with { AmountMinor = 1_001 }] }).Should().ContainSingle().Which.Should()
            .Be(new CreditReconciliationFinding(b.Company.Id, R.AdjustmentLine, writeOff.Id, -1_001, -1_000));
        Checks(b with { Adjustments = [writeOff with { Kind = CreditAdjustmentKind.OnAccountCorrection, AmountMinor = -1_000 }] }).Should().Equal(R.AdjustmentLine);
        Checks(b with { Adjustments = [writeOff with { OrderId = Guid.NewGuid() }] }).Should().Equal(R.AdjustmentLine);

        // Corrections and payouts keep the payment's balance: amount ± what they moved.
        var big = books.Pay(100_000);   // 34,000 pays the open order, 66,000 goes on account
        books.Adjust(CreditAdjustmentKind.OnAccountCorrection, -2_000, payment: big);
        books.Adjust(CreditAdjustmentKind.OnAccountCorrection, 500, payment: big);
        books.Adjust(CreditAdjustmentKind.OnAccountPayout, 3_000, payment: big);
        Check(books.Book()).Should().BeEmpty();
        cheque.Status.Should().Be(CreditPaymentStatus.Verified);
    }

    [Fact]
    public void ALineNamingARecordOfAnotherCompany_OrOfNone_IsFound()
    {
        var (books, paid, _, cheque, _) = Sample();
        var b = books.Book();
        var stranger = Guid.NewGuid();
        Check(b with { Orders = b.Orders.Select(o => o.Id == paid ? o with { CompanyId = stranger } : o).ToList() })
            .Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.ForeignRecord, paid, 0, 0));
        Checks(b with { Orders = b.Orders.Select(o => o.Id == paid ? o with { PaysOnCredit = false } : o).ToList() }).Should().Equal(R.ForeignRecord);
        Checks(b with { Orders = b.Orders.Where(o => o.Id != paid).ToList() }).Should().Equal(R.UnknownRecord);
        Checks(b with { Payments = b.Payments.Select(p => p.Id == cheque.Id ? p with { CompanyId = stranger } : p).ToList() }).Should().Equal(R.ForeignRecord);
        Checks(b with { Payments = b.Payments.Where(p => p.Id != cheque.Id).ToList() }).Should().Equal(R.UnknownRecord);
        Checks(b with { Adjustments = b.Adjustments.Select(a => a with { CompanyId = stranger }).ToList() }).Should().Equal(R.ForeignRecord);
        Checks(b with { Adjustments = [] }).Should().Equal(R.UnknownRecord);
    }

    // ---- the hold notes (B3.7) -----------------------------------------------------------------------------------------------

    [Fact]
    public void TheHoldNote_IsComparedOnlyWithALedgerThatHasNotChangedForAnHour()
    {
        var b = new Books();
        b.Charge(10_000, daysAgo: 50);   // due 20 days ago: more than 15 days past due for weeks
        Check(b.Book()).Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(b.Company.Id, R.HoldNote, null, 1, 0));
        b.HoldSince = Now.AddDays(-5);
        Check(b.Book()).Should().BeEmpty();
        Check(b.Book(), Expect(new CreditOptions { HoldAfterDaysPastDue = 0 })).Select(f => (f.Check, f.Expected, f.Actual))
            .Should().Equal([(R.HoldNote, 0L, 1L)], "with \"flag only\" every note must end");

        // Paid a moment ago: the sweep (every 15 minutes) may not have run yet, so either note is fine. Paid two hours ago: it must have.
        var fresh = new Books();
        fresh.Charge(10_000, daysAgo: 50);
        fresh.HoldSince = Now.AddDays(-5);
        fresh.Pay(10_000);
        Check(fresh.Book()).Should().BeEmpty();
        fresh.HoldSince = null;
        Check(fresh.Book()).Should().BeEmpty();
        var stale = new Books();
        stale.Charge(10_000, daysAgo: 50);
        stale.HoldSince = Now.AddDays(-5);
        stale.Pay(10_000, at: Now.AddHours(-2));
        Check(stale.Book()).Should().ContainSingle().Which.Should().Be(new CreditReconciliationFinding(stale.Company.Id, R.HoldNote, null, 0, 1));

        // Exactly 15 days past due is not held (B3.7 edge).
        var edge = new Books();
        edge.Charge(10_000, daysAgo: 45);
        Check(edge.Book()).Should().BeEmpty();
    }

    [Fact]
    public void AHoldLift_LongerThanAnAdminCanGive_IsFound()
    {
        var b = new Books();
        b.OverrideUntil = Now.AddDays(7);
        Check(b.Book()).Should().BeEmpty();
        b.OverrideUntil = Now.AddDays(8);
        Check(b.Book()).Should().ContainSingle().Which.Check.Should().Be(R.HoldOverrideTooLong);
    }

    [Fact]
    public void TheWorker_IsHostedWithTheOtherWorkers_AndTheServiceIsRegistered()
    {
        var services = new ServiceCollection().AddCatalogWorkers();
        services.Should().Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(CreditReconciliationWorker));
        new ServiceCollection().AddCatalogServices().Should().Contain(d => d.ServiceType == typeof(ICreditReconciliationService));
    }

    // ---- the Worker's schedule -----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData("2026-11-16T20:29:59Z", "2026-11-16T20:30:00Z")]
    [InlineData("2026-11-16T20:30:00Z", "2026-11-17T20:30:00Z")]
    [InlineData("2026-11-16T18:00:00Z", "2026-11-16T20:30:00Z")]   // 00:00 Dhaka: the same night
    [InlineData("2026-11-17T03:00:00Z", "2026-11-17T20:30:00Z")]
    public void TheWorker_RunsNightlyAt0230Dhaka(string now, string next) =>
        CreditReconciliationWorker.NextRunUtc(DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal))
            .Should().Be(DateTime.Parse(next, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
}
