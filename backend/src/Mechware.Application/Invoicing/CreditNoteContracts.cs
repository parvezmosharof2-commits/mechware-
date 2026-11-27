using Mechware.Application.Catalog;

namespace Mechware.Application.Invoicing;

/// <summary>One asked line: the invoice's printed <paramref name="LineNo"/>, the units (0 = value only) and optionally the value excl. VAT in poisha (null = the value of those units).</summary>
public sealed record CreditNoteLineInput(int LineNo, int Quantity, long? NetMinor = null);

/// <summary>
/// Body of <c>POST admin/invoices/{id}/credit-notes</c>: reason <c>goods_returned | price_reduction | error | other</c>, a text of 5–500 characters, 1–200 lines.
/// E6.5c: <paramref name="Replace"/> = true (reason <c>error</c> only) asks for a FULL credit note whose approval also issues the replacement 6.3 with the
/// buyer read again (ADR-0012 §9.4); without lines it credits everything left; anything less is 409 <c>INVOICE_REPLACEMENT_NEEDS_FULL_CREDIT</c>.
/// </summary>
public sealed record RequestCreditNoteRequest(string? Reason, string? ReasonText, IReadOnlyList<CreditNoteLineInput>? Lines, bool? Replace = null);

/// <summary>Body of a rejection: a reason of 5–500 characters.</summary>
public sealed record RejectCreditNoteRequest(string? Reason);

/// <summary>A credited line as asked (and, once approved, as issued), named by the original's line number.</summary>
public sealed record CreditNoteRequestLineDto(int LineNo, string Kind, string? Sku, string DescriptionEn, int Quantity, long NetMinor, long VatMinor, long TotalMinor);

/// <summary>A credit-note request. <paramref name="Status"/>: requested, approved, rejected, withdrawn. The issued 6.7 is set once approved.</summary>
public sealed record CreditNoteRequestDto(Guid Id, Guid OriginalInvoiceId, string OriginalNumber, Guid OrderId, string OrderNumber, string Status, string Reason,
    string ReasonText, IReadOnlyList<CreditNoteRequestLineDto> Lines, long TotalMinor, Guid RequestedBy, DateTime RequestedAtUtc, Guid? DecidedBy,
    DateTime? DecidedAtUtc, string? DecisionReason, Guid? IssuedInvoiceId, string? IssuedNumber, bool Replace = false);

/// <summary>One accepted line of an inspected return (F2.3b): the order line and the units accepted (≥ 1).</summary>
public sealed record ReturnCreditLine(Guid OrderItemId, int Quantity);

public sealed record RequestedCreditNote(CreditNoteRequestDto Request, bool Replayed);

/// <summary>
/// The approval's outcome: the 6.7, and its money. A credit order's charge was reduced in the same transaction (<paramref name="CreditReleasedMinor"/>
/// owed less, <paramref name="MovedOnAccountMinor"/> already paid and now on account); any other order records <paramref name="RefundDueMinor"/> = the total,
/// paid back by Finance outside this call (no gateway money moves here).
/// </summary>
public sealed record ApprovedCreditNote(CreditNoteRequestDto Request, Guid CreditNoteId, string Number, long TotalMinor, long RefundDueMinor,
    long CreditReleasedMinor, long MovedOnAccountMinor, Guid? ReplacementInvoiceId = null, string? ReplacementNumber = null);

/// <summary>
/// E6.5b, ADR-0012 §9.3, §10, §15 (threat model IT3, IT4, IT9, IT15). A request is planned against what is left but has no number and moves nothing;
/// only the approval by ANOTHER person (step-up at the endpoint) issues the 6.7, in one transaction: order → original invoice (both
/// <c>FOR NO KEY UPDATE</c>) → plan again (409 <c>CREDIT_NOTE_CHANGED</c> when it differs) → <c>ReduceChargeAsync</c> (companies) → counter (last) →
/// inserts. 404 unknown id; 409 <c>CREDIT_NOTE_FOUR_EYES</c>, <c>CREDIT_NOTE_NOT_REQUESTED</c>, <c>CREDIT_NOTE_NOT_YOURS</c>, the planner's codes,
/// <c>IDEMPOTENCY_KEY_REUSED</c>, <c>CREDIT_REDUCTION_EXCEEDS_CHARGE</c>.
/// </summary>
public interface ICreditNoteService
{
    public const int MaxPageSize = 50;

    /// <summary>Same person + same key + same body = the first request (Replayed); another body under the key = 409.</summary>
    Task<RequestedCreditNote> RequestAsync(Guid invoiceId, RequestCreditNoteRequest request, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>
    /// F2.3b: the credit-note request of an inspected return against <paramref name="invoiceId"/> (a Live 6.3 of its order), reason
    /// <c>goods_returned</c>, each order line mapped to the invoice line printing it, source key and Idempotency-Key <c>return:{returnId:N}</c>.
    /// Runs INSIDE the caller's transaction (the return's lock); the same return again answers the first request. Its approval moves the
    /// return Inspected → CreditNoted in the approving transaction. 400 <c>lines</c> (an order line not on the invoice), the planner's 409s.
    /// </summary>
    Task<CreditNoteRequestDto> RequestForReturnAsync(Guid returnId, Guid invoiceId, IReadOnlyList<ReturnCreditLine> lines, CancellationToken ct = default);

    /// <summary>The queue: requests of one status (default requested), oldest first.</summary>
    Task<PagedResult<CreditNoteRequestDto>> ListAsync(string? status, int page, int pageSize, CancellationToken ct = default);

    Task<CreditNoteRequestDto> GetAsync(Guid requestId, CancellationToken ct = default);

    Task<ApprovedCreditNote> ApproveAsync(Guid requestId, CancellationToken ct = default);

    Task<CreditNoteRequestDto> RejectAsync(Guid requestId, RejectCreditNoteRequest request, CancellationToken ct = default);

    Task<CreditNoteRequestDto> WithdrawAsync(Guid requestId, CancellationToken ct = default);
}
