using FluentAssertions;
using Mechware.Application.Credit;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Credit;

namespace Mechware.UnitTests.Credit;

/// <summary>
/// ADR-0010 §4, §5, §8.1, §8.3 and threat model §১২ (CS2, CS4, CS10, CS11, CS20, CS21, CS31): the rules of a customer payment
/// and of an adjustment, without a database.
/// </summary>
public class CreditPaymentTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);   // 15:00 on 16 November in Dhaka
    private static readonly DateOnly Today = new(2026, 11, 16);
    private static readonly CreditPaymentRules Rules = new CreditOptions().PaymentRules();
    private static readonly Guid Company = Guid.NewGuid(), Recorder = Guid.NewGuid(), Verifier = Guid.NewGuid(), Admin = Guid.NewGuid();
    private static readonly string Hash = new('a', 64);

    private static CreditPaymentInput Transfer(long amount = 50_000, string reference = "BEFTN-2026-000123", CreditPaymentMethod method = CreditPaymentMethod.Beftn) =>
        new(Company, method, amount, reference, null, Today);

    private static CreditPaymentInput Cheque(DateOnly? chequeDate = null, DateOnly? receivedOn = null) =>
        new(Company, CreditPaymentMethod.Cheque, 50_000, "000123", "Dutch-Bangla Bank", receivedOn ?? Today, chequeDate ?? receivedOn ?? Today);

    private static CreditPaymentInput Cash(long amount = 50_000) => new(Company, CreditPaymentMethod.Cash, amount, null, null, Today);

    private static CreditPayment Record(CreditPaymentInput input, CreditPaymentRules? rules = null, DateTime? now = null, string? key = null) =>
        CreditPayment.Record(Guid.NewGuid(), input, Recorder, key ?? "key-" + Guid.NewGuid().ToString("N"), Hash, rules ?? Rules, now ?? Now);

    private static void Refused(Action act, string field) =>
        act.Should().Throw<ValidationException>().Which.Errors.Should().ContainSingle(e => e.Field == field);

    [Fact]
    public void Verify_ByTheRecorder_IsRefused()
    {
        var payment = Record(Transfer());

        payment.Invoking(p => p.Verify(Recorder, Today, Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_PAYMENT_FOUR_EYES");
        payment.Invoking(p => p.Reject(Recorder, "typed the wrong amount", Now)).Should().Throw<ConflictException>()
            .Which.Code.Should().Be("CREDIT_PAYMENT_FOUR_EYES", "a decision on a payment is always someone else's (§5)");
        (payment.Status, payment.VerifiedBy, payment.ClearedOn, payment.RejectedBy).Should().Be((CreditPaymentStatus.Recorded, null, null, null));

        payment.Verify(Verifier, Today, Now);
        (payment.Status, payment.VerifiedBy, payment.VerifiedAtUtc, payment.ClearedOn).Should().Be((CreditPaymentStatus.Verified, Verifier, Now, Today));
    }

    private enum Step { Verify, Reject, Bounce }

    private static CreditPayment InStatus(CreditPaymentStatus status, CreditPaymentInput? input = null)
    {
        var p = Record(input ?? Transfer());
        if (status is CreditPaymentStatus.Verified or CreditPaymentStatus.Bounced) p.Verify(Verifier, Today, Now);
        if (status == CreditPaymentStatus.Bounced) p.Bounce(Verifier, "returned by the bank: insufficient funds", Now);
        if (status == CreditPaymentStatus.Rejected) p.Reject(Verifier, "duplicate of an earlier entry", Now);
        return p;
    }

    private static void Do(CreditPayment p, Step step)
    {
        switch (step)
        {
            case Step.Verify: p.Verify(Admin, Today, Now); break;
            case Step.Reject: p.Reject(Admin, "entered by mistake", Now); break;
            default: p.Bounce(Admin, "returned by the bank", Now); break;
        }
    }

    [Fact]
    public void EveryIllegalPaymentTransition_IsRefused_AndChangesNothing()
    {
        var legal = new HashSet<(CreditPaymentStatus, Step)>
        {
            (CreditPaymentStatus.Recorded, Step.Verify), (CreditPaymentStatus.Recorded, Step.Reject), (CreditPaymentStatus.Verified, Step.Bounce)
        };
        foreach (var status in Enum.GetValues<CreditPaymentStatus>())
            foreach (var step in Enum.GetValues<Step>())
            {
                var p = InStatus(status);
                var before = (p.Status, p.VerifiedBy, p.RejectedBy, p.BouncedBy, p.DecisionReason, p.UpdatedAtUtc);
                if (legal.Contains((status, step))) { Do(p, step); p.Status.Should().NotBe(status); continue; }

                FluentActions.Invoking(() => Do(p, step)).Should().Throw<ConflictException>($"{status} → {step}")
                    .Which.Code.Should().StartWith("CREDIT_PAYMENT_NOT_");
                (p.Status, p.VerifiedBy, p.RejectedBy, p.BouncedBy, p.DecisionReason, p.UpdatedAtUtc).Should().Be(before);
            }

        var cash = InStatus(CreditPaymentStatus.Verified, Cash());
        cash.Invoking(p => p.Bounce(Admin, "returned by the bank", Now)).Should().Throw<ConflictException>()
            .Which.Code.Should().Be("CREDIT_PAYMENT_CASH_CANNOT_BOUNCE");
        Refused(() => InStatus(CreditPaymentStatus.Verified).Bounce(Admin, "    ", Now), "reason");
        Refused(() => Record(Transfer()).Reject(Admin, "no", Now), "reason");
    }

    [Fact]
    public void PostDatedCheque_CannotBeVerifiedBeforeItsDate()
    {
        var chequeDate = Today.AddDays(10);
        var payment = Record(Cheque(chequeDate));

        foreach (var day in new[] { 0, 9 })
            payment.Invoking(p => p.Verify(Verifier, Today.AddDays(day), Now.AddDays(day))).Should().Throw<ConflictException>()
                .Which.Code.Should().Be("CREDIT_PAYMENT_CHEQUE_NOT_DUE");
        // 23:59 on the day before the cheque date in Dhaka (17:59 UTC): still too early.
        payment.Invoking(p => p.Verify(Verifier, Today.AddDays(9), new DateTime(2026, 11, 25, 17, 59, 0, DateTimeKind.Utc)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_PAYMENT_CHEQUE_NOT_DUE");
        payment.Status.Should().Be(CreditPaymentStatus.Recorded);

        payment.Verify(Verifier, chequeDate, new DateTime(2026, 11, 25, 18, 0, 0, DateTimeKind.Utc));   // midnight in Dhaka: the cheque date
        payment.Status.Should().Be(CreditPaymentStatus.Verified);

        Refused(() => Record(Cheque(chequeDate), Rules with { AcceptPostDatedCheques = false }), "chequeDate");
        Record(Cheque(Today), Rules with { AcceptPostDatedCheques = false }).ChequeDate.Should().Be(Today, "a cheque due today is not post-dated");
    }

    [Fact]
    public void ClearedOn_BeforeTheChequeDate_OrInTheFuture_IsRefused()
    {
        var cheque = Record(Cheque(Today.AddDays(-3), Today.AddDays(-5)));   // received on the 11th, dated the 13th
        Refused(() => cheque.Verify(Verifier, Today.AddDays(-4), Now), "clearedOn");
        Refused(() => cheque.Verify(Verifier, Today.AddDays(1), Now), "clearedOn");
        cheque.Verify(Verifier, Today.AddDays(-3), Now);

        var transfer = Record(Transfer() with { ReceivedOn = Today.AddDays(-2) });
        Refused(() => transfer.Verify(Verifier, Today.AddDays(-3), Now), "clearedOn");   // before the money was received
        transfer.Verify(Verifier, Today.AddDays(-2), Now);
    }

    [Fact]
    public void ReceivedOn_InTheFuture_IsRefused()
    {
        Refused(() => Record(Transfer() with { ReceivedOn = Today.AddDays(1) }), "receivedOn");
        // 17 November starts in Dhaka at 18:00 UTC on the 16th.
        Refused(() => Record(Transfer() with { ReceivedOn = Today.AddDays(1) }, now: new DateTime(2026, 11, 16, 17, 59, 59, DateTimeKind.Utc)), "receivedOn");
        Record(Transfer() with { ReceivedOn = Today.AddDays(1) }, now: new DateTime(2026, 11, 16, 18, 0, 0, DateTimeKind.Utc))
            .ReceivedOn.Should().Be(Today.AddDays(1));
    }

    [Fact]
    public void ReceivedOn_OlderThan30Days_NeedsALateEntryReason()
    {
        Record(Transfer() with { ReceivedOn = Today.AddDays(-30) }).LateEntryReason.Should().BeNull("30 days is still on time");
        Refused(() => Record(Transfer() with { ReceivedOn = Today.AddDays(-31) }), "lateEntryReason");
        Refused(() => Record(Transfer() with { ReceivedOn = Today.AddDays(-31), LateEntryReason = "123456789" }), "lateEntryReason");
        Refused(() => Record(Transfer() with { LateEntryReason = "the slip arrived late by post" }), "lateEntryReason");

        Record(Transfer() with { ReceivedOn = Today.AddDays(-400), LateEntryReason = "  slip found in the March file  " })
            .LateEntryReason.Should().Be("slip found in the March file");
    }

    [Fact]
    public void ChequeDate_And_DrawerBank_FollowTheMethod()
    {
        Refused(() => Record(Cheque() with { ChequeDate = null }), "chequeDate");
        Refused(() => Record(Transfer() with { ChequeDate = Today }), "chequeDate");
        Refused(() => Record(Cheque(Today.AddDays(-181))), "chequeDate");                                    // stale
        Refused(() => Record(Cheque(Today.AddDays(181))), "chequeDate");
        Record(Cheque(Today.AddDays(-180))).ChequeDate.Should().Be(Today.AddDays(-180));
        Refused(() => Record(Cheque() with { DrawerBank = " - " }), "drawerBank");
        Refused(() => Record(Transfer() with { DrawerBank = "Dutch-Bangla Bank" }), "drawerBank");
        Refused(() => Record(new CreditPaymentInput(Company, CreditPaymentMethod.BankDeposit, 1_000, "SLIP-778", null, Today)), "drawerBank");

        var deposit = Record(new CreditPaymentInput(Company, CreditPaymentMethod.BankDeposit, 1_000, "slip 778", "Bank Asia Ltd.", Today));
        (deposit.Reference, deposit.DrawerBank, deposit.ChequeDate).Should().Be(("SLIP778", "BANKASIALTD", null));
    }

    [Theory]
    [InlineData(CreditPaymentMethod.Beftn, 0)]
    [InlineData(CreditPaymentMethod.Beftn, -1)]
    [InlineData(CreditPaymentMethod.Beftn, long.MinValue)]
    [InlineData(CreditPaymentMethod.Beftn, 10_000_000_001)]          // above Credit:MaxPaymentMinor (BDT 10 crore)
    [InlineData(CreditPaymentMethod.Cash, 20_000_001)]               // above Credit:MaxCashPaymentMinor (BDT 2,00,000)
    public void PaymentAmount_OutsideOneToMax_IsRefused(CreditPaymentMethod method, long amount)
    {
        var input = method == CreditPaymentMethod.Cash ? Cash(amount) : Transfer(amount);
        Refused(() => Record(input), "amountMinor");
    }

    [Fact]
    public void PaymentAmount_AtTheLimits_IsAccepted()
    {
        Record(Transfer(1)).AmountMinor.Should().Be(1);
        Record(Transfer(10_000_000_000)).AmountMinor.Should().Be(10_000_000_000);
        Record(Cash(20_000_000)).AmountMinor.Should().Be(20_000_000);
        Refused(() => Record(Cash(20_000_000), Rules with { MaxPaymentMinor = 19_999_999 }), "amountMinor");
    }

    [Fact]
    public void AMethodNotInTheConfiguredList_IsRefused()
    {
        new CreditOptions().PaymentRules().Methods.Should().BeEquivalentTo(Enum.GetValues<CreditPaymentMethod>(), "the default accepts all nine");
        var rules = new CreditOptions { PaymentMethods = " cheque , pay_order,bank_deposit,beftn" }.PaymentRules();
        rules.Methods.Should().BeEquivalentTo([CreditPaymentMethod.Cheque, CreditPaymentMethod.PayOrder, CreditPaymentMethod.BankDeposit, CreditPaymentMethod.Beftn]);

        Record(Transfer(), rules).Method.Should().Be(CreditPaymentMethod.Beftn);
        Refused(() => Record(Transfer(method: CreditPaymentMethod.Bkash, reference: "9A7B6C5D4E"), rules), "method");
        Refused(() => Record(Cash(), rules), "method");
        Refused(() => Record(Transfer() with { Method = (CreditPaymentMethod)42 }), "method");
    }

    [Theory]
    [InlineData("ab-123", "AB123")]
    [InlineData("AB 123", "AB123")]
    [InlineData("  a/b.1_2:3 ", "AB123")]
    [InlineData("ＡＢ１２３", "AB123")]          // full-width forms
    [InlineData("AB১২৩", "AB123")]              // Bengali digits
    [InlineData("ab\t12 3", "AB123")]      // tab, no-break space
    public void References_AreNormalised_SoDifferentSpellingsAreOneKey(string typed, string key) =>
        Record(Transfer(reference: typed)).Reference.Should().Be(key);

    [Theory]
    [InlineData("АB123")]                       // a Cyrillic А that looks like A
    [InlineData("রেফ১২৩৪")]                     // Bengali letters
    [InlineData("AB1")]                         // fewer than 4 letters or digits
    [InlineData("--- ---")]
    [InlineData(null)]
    [InlineData("12345678901234567890123456789012345678901")]   // 41
    public void References_ThatAreNotLettersAndDigits_OrOfTheWrongLength_AreRefused(string? typed) =>
        Refused(() => Record(Transfer(reference: typed!)), "reference");

    [Theory]
    [InlineData(CreditPaymentMethod.Bkash, "01711-234567")]
    [InlineData(CreditPaymentMethod.Nagad, "+880 1711 234567")]
    public void AWalletPhoneNumber_IsRefused_OnlyTheTransactionIdIsStored(CreditPaymentMethod wallet, string phone)
    {
        Refused(() => Record(Transfer(method: wallet, reference: phone)), "reference");
        Record(Transfer(method: wallet, reference: "9a7b6c5d4e")).Reference.Should().Be("9A7B6C5D4E");
        Record(Transfer(reference: "01711234567")).Reference.Should().Be("01711234567", "only wallets carry a phone number");
    }

    [Fact]
    public void Cash_GetsAReceiptNumber_AndTakesNoReference()
    {
        var id = Guid.Parse("a1b2c3d4-0000-4000-8000-000000000001");
        var cash = CreditPayment.Record(id, Cash(), Recorder, "key-cash-0001", Hash, Rules, Now);
        cash.Reference.Should().Be("CR261116A1B2C3", "CR-yyMMdd-XXXXXX, normalised");
        Refused(() => Record(Cash() with { Reference = "R-1" }), "reference");
    }

    [Fact]
    public void Allocations_NeverExceedThePayment()
    {
        // CS6 at recording; the planner checks the same at verification (CreditAllocationPlannerTests).
        var a = Guid.NewGuid();
        var b = Guid.NewGuid();
        Refused(() => Record(Transfer(1_000) with { Allocations = [new(a, 600), new(b, 401)] }), "allocations");
        Refused(() => Record(Transfer(1_000) with { Allocations = [new(a, 1_001)] }), "allocations");
        Refused(() => Record(Transfer(1_000) with { Allocations = [new(a, 0)] }), "allocations");
        Refused(() => Record(Transfer(1_000) with { Allocations = [new(a, 300), new(a, 300)] }), "allocations");
        Refused(() => Record(Transfer(1_000) with { Allocations = [new(a, long.MaxValue), new(b, long.MaxValue)] }), "allocations");
        Refused(() => Record(Transfer(1_000) with { Allocations = [.. Enumerable.Range(0, 51).Select(_ => new CreditAllocationRequest(Guid.NewGuid(), 1))] }), "allocations");

        Record(Transfer(1_000) with { Allocations = [new(a, 600), new(b, 400)] }).Allocations.Should().Equal(new(a, 600), new CreditAllocationRequest(b, 400));
        Record(Transfer(1_000) with { Allocations = [.. Enumerable.Range(0, 50).Select(_ => new CreditAllocationRequest(Guid.NewGuid(), 20))] })
            .Allocations.Should().HaveCount(50);
    }

    [Fact]
    public void IdempotencyKeyAndRequestHash_AreRequired()
    {
        Refused(() => Record(Transfer(), key: "short"), "idempotencyKey");
        Refused(() => Record(Transfer(), key: "key with spaces"), "idempotencyKey");
        Refused(() => Record(Transfer(), key: new string('k', 101)), "idempotencyKey");
        Refused(() => CreditPayment.Record(Guid.NewGuid(), Transfer(), Recorder, "key-12345678", Hash.ToUpperInvariant(), Rules, Now), "requestHash");
        Refused(() => CreditPayment.Record(Guid.NewGuid(), Transfer(), Recorder, "key-12345678", "abc", Rules, Now), "requestHash");
        var p = Record(Transfer(), key: "key-12345678");
        (p.IdempotencyKey, p.RequestHash, p.RecordedBy, p.CreatedAtUtc, p.Status).Should().Be(("key-12345678", Hash, Recorder, Now, CreditPaymentStatus.Recorded));
    }

    // ---- Adjustments (§8.3) ----

    private static CreditAdjustmentInput WriteOff(long amount = 5_000) =>
        new(Company, CreditAdjustmentKind.WriteOff, amount, "customer closed; bad debt approved by the board", OrderId: Guid.NewGuid());

    private static CreditAdjustment Request(CreditAdjustmentInput input) =>
        CreditAdjustment.Request(Guid.NewGuid(), input, Recorder, Rules.MaxPaymentMinor, Now);

    [Fact]
    public void Approve_ByTheRequester_OrByWhoeverRecordedOrVerifiedItsPayment_IsRefused()
    {
        var writeOff = Request(WriteOff());
        writeOff.Invoking(a => a.Approve(Recorder, Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_ADJUSTMENT_FOUR_EYES");
        writeOff.Status.Should().Be(CreditAdjustmentStatus.Requested);
        writeOff.Approve(Admin, Now);
        (writeOff.Status, writeOff.ApprovedBy, writeOff.ApprovedAtUtc).Should().Be((CreditAdjustmentStatus.Approved, Admin, Now));

        var payment = InStatus(CreditPaymentStatus.Verified);   // recorded by Recorder, verified by Verifier
        var payout = CreditAdjustment.Request(Guid.NewGuid(), new(Company, CreditAdjustmentKind.OnAccountPayout, 1_000,
            "over-payment sent back on request", PaymentId: payment.Id, PayoutReference: "mw-out-0001"), Admin, Rules.MaxPaymentMinor, Now);
        payout.PayoutReference.Should().Be("MWOUT0001");
        foreach (var colleague in new[] { Recorder, Verifier, Admin })
            payout.Invoking(a => a.Approve(colleague, Now, payment)).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_ADJUSTMENT_FOUR_EYES");
        payout.Invoking(a => a.Approve(Guid.NewGuid(), Now)).Should().Throw<InvalidOperationException>("its payment must be passed to check who touched it");
        payout.Approve(Guid.NewGuid(), Now, payment);
        payout.Status.Should().Be(CreditAdjustmentStatus.Approved);
    }

    [Fact]
    public void EveryIllegalAdjustmentTransition_IsRefused()
    {
        foreach (var decided in new Action<CreditAdjustment>[] { a => a.Approve(Admin, Now), a => a.Reject(Admin, "not needed after all", Now) })
        {
            var adjustment = Request(WriteOff());
            decided(adjustment);
            var before = (adjustment.Status, adjustment.ApprovedBy, adjustment.RejectedBy, adjustment.DecisionReason);
            adjustment.Invoking(a => a.Approve(Verifier, Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_ADJUSTMENT_NOT_REQUESTED");
            adjustment.Invoking(a => a.Reject(Verifier, "changed my mind", Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("CREDIT_ADJUSTMENT_NOT_REQUESTED");
            (adjustment.Status, adjustment.ApprovedBy, adjustment.RejectedBy, adjustment.DecisionReason).Should().Be(before);
        }

        var own = Request(WriteOff());
        own.Reject(Recorder, "withdrawn: wrong order", Now);   // a rejection moves no money: the requester may withdraw
        (own.Status, own.RejectedBy, own.DecisionReason).Should().Be((CreditAdjustmentStatus.Rejected, Recorder, "withdrawn: wrong order"));
        Refused(() => Request(WriteOff()).Reject(Admin, "no", Now), "reason");
    }

    public static TheoryData<CreditAdjustmentKind, long, bool, bool, bool, string?> BadAdjustments => new()
    {
        // kind, amount, order, payment, credit note, payout reference
        { CreditAdjustmentKind.WriteOff, -5_000, true, false, false, null },            // a write-off only lowers a debt (never raises one)
        { CreditAdjustmentKind.WriteOff, 0, true, false, false, null },
        { CreditAdjustmentKind.WriteOff, 5_000, false, false, false, null },            // names its order
        { CreditAdjustmentKind.WriteOff, 5_000, true, true, false, null },
        { CreditAdjustmentKind.OnAccountCorrection, 0, false, true, false, null },
        { CreditAdjustmentKind.OnAccountCorrection, 5_000, false, false, false, null }, // names one source
        { CreditAdjustmentKind.OnAccountCorrection, 5_000, false, true, true, null },
        { CreditAdjustmentKind.OnAccountCorrection, 5_000, true, true, false, null },   // never an order's debt
        { CreditAdjustmentKind.OnAccountPayout, -5_000, false, true, false, "MW-OUT-1" },
        { CreditAdjustmentKind.OnAccountPayout, 5_000, false, true, false, null },      // MECHWARE's transfer reference
        { (CreditAdjustmentKind)9, 5_000, true, false, false, null },
    };

    [Theory]
    [MemberData(nameof(BadAdjustments))]
    public void AnAdjustment_OfTheWrongShape_IsRefused(CreditAdjustmentKind kind, long amount, bool order, bool payment, bool note, string? payoutReference) =>
        FluentActions.Invoking(() => Request(new(Company, kind, amount, "a reason long enough", order ? Guid.NewGuid() : null,
            payment ? Guid.NewGuid() : null, note ? Guid.NewGuid() : null, payoutReference))).Should().Throw<ValidationException>();

    [Fact]
    public void AnAdjustment_NeedsAReasonAndAnAmountWithinTheLimit()
    {
        Refused(() => Request(WriteOff() with { Reason = "too short" }), "reason");
        Refused(() => Request(WriteOff() with { Reason = new string('r', 501) }), "reason");
        Refused(() => Request(WriteOff(Rules.MaxPaymentMinor + 1)), "amountMinor");
        Refused(() => Request(WriteOff() with { PayoutReference = "MW-OUT-1" }), "payoutReference");
        Refused(() => Request(new(Company, CreditAdjustmentKind.OnAccountCorrection, -(Rules.MaxPaymentMinor + 1), "mis-posted deposit", PaymentId: Guid.NewGuid())), "amountMinor");

        var correction = Request(new(Company, CreditAdjustmentKind.OnAccountCorrection, -2_500, "  mis-posted deposit  ", CreditNoteId: Guid.NewGuid()));
        (correction.AmountMinor, correction.Reason, correction.RequestedBy, correction.Status).Should()
            .Be((-2_500, "mis-posted deposit", Recorder, CreditAdjustmentStatus.Requested));
    }
}
