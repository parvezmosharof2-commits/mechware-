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

    private void Expect(CreditAdjustmentStatus status)
    {
        if (Status != status) throw new ConflictException("CREDIT_ADJUSTMENT_NOT_REQUESTED", $"A {Status} adjustment cannot be decided again.");
    }
}
