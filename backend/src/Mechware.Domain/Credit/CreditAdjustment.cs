using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Credit;

/// <summary>What an adjustment does (ADR-0010 §8.3). Stored snake_case.</summary>
public enum CreditAdjustmentKind { WriteOff = 1, OnAccountCorrection = 2, OnAccountPayout = 3 }

public enum CreditAdjustmentStatus { Requested = 1, Approved = 2, Rejected = 3 }

/// <summary>
/// What finance asks for. A write-off names one order (amount &gt; 0 lowers its receivable); a correction (± amount) or a
/// payout (amount &gt; 0, with MECHWARE's own bank transfer reference) names one source of on-account money.
/// </summary>
public sealed record CreditAdjustmentInput(Guid CompanyId, CreditAdjustmentKind Kind, long AmountMinor, string Reason, Guid? OrderId = null,
    Guid? PaymentId = null, Guid? CreditNoteId = null, string? PayoutReference = null);

/// <summary>
/// A staff correction of a company's credit (ADR-0010 §8.3, threat model CS20, CS21): requested by one user, approved by a
/// different one who also did not record or verify the payment it touches (domain; the database CHECKs approver ≠ requester).
/// Only the approval posts ledger lines (B3.6), and the ledger caps them by what is outstanding or on account. An adjustment
/// never raises a receivable (§2.2). Changes only by its next step (<c>CreditLedgerSql.InstallSettlement</c>).
/// </summary>
public sealed class CreditAdjustment : Entity<Guid>
{
    public const int MinReasonLength = 10, MaxReasonLength = 500;

    public Guid CompanyId { get; private set; }
    public CreditAdjustmentKind Kind { get; private set; }
    /// <summary>Whole poisha; negative only for a correction that takes on-account money away.</summary>
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = CreditLedgerEntry.BdtCurrency;
    public Guid? OrderId { get; private set; }
    public Guid? PaymentId { get; private set; }
    public Guid? CreditNoteId { get; private set; }
    public string? PayoutReference { get; private set; }
    public string Reason { get; private set; } = string.Empty;
    public CreditAdjustmentStatus Status { get; private set; } = CreditAdjustmentStatus.Requested;
    public Guid RequestedBy { get; private set; }
    public Guid? ApprovedBy { get; private set; }
    public DateTime? ApprovedAtUtc { get; private set; }
    public Guid? RejectedBy { get; private set; }
    public DateTime? RejectedAtUtc { get; private set; }
    public string? DecisionReason { get; private set; }

    private CreditAdjustment() { }

    private CreditAdjustment(Guid id) : base(id) { }

    /// <param name="maxAmountMinor">The largest amount either way: <c>Credit:MaxPaymentMinor</c>.</param>
    public static CreditAdjustment Request(Guid id, CreditAdjustmentInput input, Guid requestedBy, long maxAmountMinor, DateTime nowUtc)
    {
        var (k, x) = (input.Kind, input.AmountMinor);
        var oneSource = input.OrderId is null && (input.PaymentId is null) != (input.CreditNoteId is null);
        var shaped = k switch
        {
            CreditAdjustmentKind.WriteOff => x > 0 && input.OrderId is not null && input.PaymentId is null && input.CreditNoteId is null,
            CreditAdjustmentKind.OnAccountCorrection => x != 0 && oneSource,
            CreditAdjustmentKind.OnAccountPayout => x > 0 && oneSource,
            _ => false
        };
        if (!shaped)
            throw new ValidationException("kind", "A write-off lowers one order; a correction (±) or a payout (+) moves the on-account money of one source.");
        if (x < -maxAmountMinor || x > maxAmountMinor) throw new ValidationException("amountMinor", $"The amount is at most {maxAmountMinor} poisha.");
        if (k != CreditAdjustmentKind.OnAccountPayout && !string.IsNullOrWhiteSpace(input.PayoutReference))
            throw new ValidationException("payoutReference", "Only a payout has MECHWARE's bank transfer reference.");
        var reason = (input.Reason ?? string.Empty).Trim();
        if (reason.Length is < MinReasonLength or > MaxReasonLength)
            throw new ValidationException("reason", $"Give a reason of {MinReasonLength} to {MaxReasonLength} characters.");

        return new CreditAdjustment(id)
        {
            CompanyId = input.CompanyId,
            Kind = k,
            AmountMinor = x,
            OrderId = input.OrderId,
            PaymentId = input.PaymentId,
            CreditNoteId = input.CreditNoteId,
            Reason = reason,
            RequestedBy = requestedBy,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
            PayoutReference = k == CreditAdjustmentKind.OnAccountPayout
                ? CreditPayment.Normalise(input.PayoutReference, "payoutReference", CreditPayment.MinReferenceLength, CreditPayment.MaxReferenceLength)
                : null
        };
    }

    /// <summary>Four-eyes (CS20): not the requester, nor whoever recorded or verified <paramref name="source"/>, its payment.</summary>
    public void Approve(Guid approverId, DateTime nowUtc, CreditPayment? source = null)
    {
        Expect(CreditAdjustmentStatus.Requested);
        if (source?.Id != PaymentId) throw new InvalidOperationException("Pass the adjustment's own source payment, and only that.");
        if (approverId == RequestedBy || approverId == source?.RecordedBy || approverId == source?.VerifiedBy)
            throw new ConflictException("CREDIT_ADJUSTMENT_FOUR_EYES", "Whoever requested an adjustment, or recorded or verified its payment, cannot approve it.");
        (Status, ApprovedBy, ApprovedAtUtc, UpdatedAtUtc) = (CreditAdjustmentStatus.Approved, approverId, nowUtc, nowUtc);
    }

    /// <summary>Any user allowed to decide, the requester included (a withdrawal): a rejection moves no money.</summary>
    public void Reject(Guid userId, string reason, DateTime nowUtc)
    {
        Expect(CreditAdjustmentStatus.Requested);
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < 5 or > MaxReasonLength) throw new ValidationException("reason", $"Give a reason of 5 to {MaxReasonLength} characters.");
        (Status, RejectedBy, RejectedAtUtc, DecisionReason, UpdatedAtUtc) = (CreditAdjustmentStatus.Rejected, userId, nowUtc, trimmed, nowUtc);
    }

    /// <summary>
    /// The one ledger line this adjustment posts (§8.3, B3.6), checked against <paramref name="lines"/> (the company's lines of its
    /// order, or of its source). Never clamped, always 409: a write-off at most what the order still owes
    /// (<c>CREDIT_EXCEEDS_RECEIVABLE</c>); a payout or a negative correction at most what the source holds on account
    /// (<c>CREDIT_ON_ACCOUNT_INSUFFICIENT</c>); a positive correction only gives back what earlier corrections took from that source
    /// (<c>CREDIT_CORRECTION_EXCEEDS_TAKEN</c>: no adjustment creates money); a payment source must still be verified, and the money
    /// of a payment that can still bounce (not cash) is paid out only <paramref name="payoutWaitDays"/> after it cleared
    /// (<c>CREDIT_PAYOUT_TOO_EARLY</c>: a later bounce cannot take back money already paid out). Run at the request (an early
    /// answer) and again at the approval under the locks. Pure.
    /// </summary>
    public CreditPosting Posting(IEnumerable<CreditLedgerEntry> lines, CreditPayment? source, int payoutWaitDays, CreditEventInfo e)
    {
        if (source?.Id != PaymentId) throw new InvalidOperationException("Pass the adjustment's own source payment, and only that.");
        const string notFound = "CREDIT_ADJUSTMENT_TARGET_NOT_FOUND";
        var mine = lines.Where(l => l.CompanyId == CompanyId).ToList();
        if (Kind == CreditAdjustmentKind.WriteOff)
        {
            var order = mine.Where(l => l.OrderId == OrderId).ToList();
            if (!order.Any(l => l.Kind == CreditLedgerKind.Charge)) throw new ConflictException(notFound, "This order has no charge on the company's credit.");
            var owed = order.Sum(l => l.ReceivableDeltaMinor);
            if (AmountMinor > owed) throw new ConflictException("CREDIT_EXCEEDS_RECEIVABLE", $"The order still owes {owed} poisha, less than {AmountMinor}.");
            return Line(CreditLedgerKind.WriteOff, -AmountMinor, 0, e);
        }

        if (source is not null && source.CompanyId != CompanyId) throw new ConflictException(notFound, "This payment is not the company's.");
        if (source is not null && source.Status != CreditPaymentStatus.Verified)
            throw new ConflictException("CREDIT_ADJUSTMENT_SOURCE_NOT_VERIFIED", "Only the money of a verified payment can be corrected or paid out.");
        var held = mine.Where(l => l.PaymentId == PaymentId && l.CreditNoteId == CreditNoteId).ToList();
        var payout = Kind == CreditAdjustmentKind.OnAccountPayout;
        if (payout && source is { Method: not CreditPaymentMethod.Cash, ClearedOn: { } cleared } && cleared.AddDays(payoutWaitDays) > e.OccurredOn)
            throw new ConflictException("CREDIT_PAYOUT_TOO_EARLY", $"This money can still bounce: pay it out from {cleared.AddDays(payoutWaitDays):yyyy-MM-dd}.");
        var onAccount = held.Sum(l => l.OnAccountDeltaMinor);
        if ((payout || AmountMinor < 0) && Math.Abs(AmountMinor) > onAccount)
            throw new ConflictException("CREDIT_ON_ACCOUNT_INSUFFICIENT", $"This source holds {onAccount} poisha on account, less than {Math.Abs(AmountMinor)}.");
        var taken = -held.Where(l => l.Kind == CreditLedgerKind.OnAccountCorrection).Sum(l => l.OnAccountDeltaMinor);
        if (!payout && AmountMinor > taken)
            throw new ConflictException("CREDIT_CORRECTION_EXCEEDS_TAKEN", $"A correction only gives back what corrections took from this source ({taken} poisha).");
        return payout ? Line(CreditLedgerKind.OnAccountPayout, 0, -AmountMinor, e) : Line(CreditLedgerKind.OnAccountCorrection, 0, AmountMinor, e);
    }

    private CreditPosting Line(CreditLedgerKind kind, long r, long a, CreditEventInfo e) =>
        new(kind, r, a, CreditLedgerKeys.Adjust(Id), e.OccurredOn, OrderId, PaymentId, Id, CreditNoteId, EventId: e.EventId,
            ActorUserId: e.ActorUserId, CorrelationId: e.CorrelationId, Reason: e.Reason);

    private void Expect(CreditAdjustmentStatus status)
    {
        if (Status != status) throw new ConflictException("CREDIT_ADJUSTMENT_NOT_REQUESTED", $"A {Status} adjustment cannot be decided again.");
    }
}
