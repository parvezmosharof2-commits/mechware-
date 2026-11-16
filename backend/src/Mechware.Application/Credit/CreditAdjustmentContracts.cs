using System.Text.Json.Serialization;

namespace Mechware.Application.Credit;

/// <summary>
/// <c>POST admin/companies/{companyId}/credit/adjustments</c> (ADR-0010 §8.3, B3.6). <c>kind</c> is <c>write_off</c> (an
/// <c>orderId</c>, amount &gt; 0), <c>on_account_correction</c> (± amount, one source: <c>paymentId</c> or <c>creditNoteId</c>) or
/// <c>on_account_payout</c> (amount &gt; 0, one source, MECHWARE's own bank transfer <c>payoutReference</c>); reason 10-500.
/// Strict numbers (CS10): an amount is a JSON integer.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record RequestCreditAdjustmentRequest(string? Kind, long AmountMinor, string? Reason, Guid? OrderId = null, Guid? PaymentId = null,
    Guid? CreditNoteId = null, string? PayoutReference = null, string? Currency = null);

/// <summary>Why the adjustment is refused or withdrawn (5-500 characters).</summary>
public sealed record RejectCreditAdjustmentRequest(string? Reason);

/// <summary>Staff view of one adjustment. Never sent to a buyer (staff ids, internal reason).</summary>
public sealed record CreditAdjustmentDto(Guid Id, Guid CompanyId, string Kind, long AmountMinor, string Currency, Guid? OrderId, Guid? PaymentId,
    Guid? CreditNoteId, string? PayoutReference, string Reason, string Status, Guid RequestedBy, DateTime RequestedAtUtc, Guid? ApprovedBy,
    DateTime? ApprovedAtUtc, Guid? RejectedBy, DateTime? RejectedAtUtc, string? DecisionReason);

/// <summary><paramref name="Replayed"/>: the same person sent the same Idempotency-Key and body again; nothing new was stored.</summary>
public sealed record RequestedCreditAdjustment(CreditAdjustmentDto Adjustment, bool Replayed);

/// <summary>What one approval posted: its line's two deltas and the company's totals afterwards.</summary>
public sealed record ApprovedCreditAdjustment(CreditAdjustmentDto Adjustment, Guid EventId, long ReceivableDeltaMinor, long OnAccountDeltaMinor,
    long CreditUsedMinor, long CreditOnAccountMinor);

/// <summary>
/// Staff adjustments of a company's credit (ADR-0010 §8.3, threat model CS20, CS21): a request (<c>credit.adjust</c>) moves no
/// money; only the approval by someone else (<c>credit.adjust_approve</c> + step-up), who also did not record or verify the source
/// payment, writes the one ledger line, with an audit row and an outbox event, in one transaction.
/// </summary>
public interface ICreditAdjustmentService
{
    /// <summary>
    /// Idempotency-Key 8-100 per staff member: a replay returns the first adjustment, another body 409 <c>IDEMPOTENCY_KEY_REUSED</c>.
    /// 404 unless the company is Active or Suspended; 409 <c>CREDIT_ADJUSTMENT_TARGET_NOT_FOUND</c> and the caps of the approval
    /// (checked early here, and again when approved).
    /// </summary>
    Task<RequestedCreditAdjustment> RequestAsync(Guid companyId, RequestCreditAdjustmentRequest request, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>409 <c>CREDIT_ADJUSTMENT_FOUR_EYES</c>, <c>CREDIT_ADJUSTMENT_NOT_REQUESTED</c> (also for a second approval), and the caps.</summary>
    Task<ApprovedCreditAdjustment> ApproveAsync(Guid adjustmentId, CancellationToken ct = default);

    /// <summary>Anyone with <c>credit.adjust</c>, the requester included (a withdrawal): a rejection moves no money.</summary>
    Task<CreditAdjustmentDto> RejectAsync(Guid adjustmentId, RejectCreditAdjustmentRequest request, CancellationToken ct = default);
}
