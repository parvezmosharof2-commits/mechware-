using System.Text.Json.Serialization;
using Mechware.Domain.Credit;

namespace Mechware.Application.Credit;

/// <summary>
/// <c>POST admin/companies/{companyId}/credit/payments</c> (ADR-0010 §4). Amounts are whole poisha and JSON integers only;
/// <c>method</c> is snake_case (<c>cheque</c>, <c>pay_order</c>, <c>beftn</c>, …); <c>currency</c> may be left out or "BDT".
/// Strict numbers: the API's web defaults would read <c>"100"</c> as 100, and an amount must be a JSON integer (CS10).
/// </summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record RecordCreditPaymentRequest(string? Method, long AmountMinor, DateOnly? ReceivedOn, string? Reference = null,
    string? DrawerBank = null, DateOnly? ChequeDate = null, string? LateEntryReason = null,
    IReadOnlyList<CreditAllocationInput>? Allocations = null, string? Note = null, string? Currency = null);

/// <summary>An order the payment should go to first, and how much (at most 50, each order once).</summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CreditAllocationInput(Guid OrderId, long AmountMinor);

/// <summary>The Dhaka date the money was in MECHWARE's bank.</summary>
public sealed record VerifyCreditPaymentRequest(DateOnly? ClearedOn);

public sealed record RejectCreditPaymentRequest(string? Reason);

/// <summary>Staff view of one payment. Never sent to a buyer (it carries staff ids and the internal note).</summary>
public sealed record CreditPaymentDto(Guid Id, Guid CompanyId, string Method, long AmountMinor, string Currency, string Reference,
    string? DrawerBank, DateOnly ReceivedOn, DateOnly? ChequeDate, string? LateEntryReason, IReadOnlyList<CreditAllocationRequest> Allocations,
    string? Note, string Status, Guid RecordedBy, DateTime RecordedAtUtc, Guid? VerifiedBy, DateTime? VerifiedAtUtc, DateOnly? ClearedOn,
    Guid? RejectedBy, DateTime? RejectedAtUtc, string? DecisionReason);

/// <summary><paramref name="Replayed"/>: the same recorder sent the same Idempotency-Key and body again; nothing new was stored.</summary>
public sealed record RecordedCreditPayment(CreditPaymentDto Payment, bool Replayed);

/// <summary>What one verification released (§7): the orders paid, the rest on account, the company's totals afterwards.</summary>
public sealed record VerifiedCreditPayment(CreditPaymentDto Payment, Guid EventId, IReadOnlyList<CreditAllocation> Applied, long OnAccountMinor,
    long CreditUsedMinor, long CreditOnAccountMinor);

/// <summary>
/// Customer payments on credit (ADR-0010 §4-§7; B3.4). The actor is the signed-in staff member. Recording changes no credit;
/// verification by someone other than the recorder releases it, in one transaction with its ledger lines, audit row and outbox event.
/// </summary>
public interface ICreditPaymentService
{
    /// <summary>404 unless the company is Active or Suspended; 409 <c>CREDIT_PAYMENT_DUPLICATE_REFERENCE</c>, <c>IDEMPOTENCY_KEY_REUSED</c>.</summary>
    Task<RecordedCreditPayment> RecordAsync(Guid companyId, RecordCreditPaymentRequest request, string? idempotencyKey, CancellationToken ct = default);

    /// <summary>Owner decision 3: every verification (default), or only from <c>Credit:VerifyStepUpFromMinor</c>.</summary>
    Task<bool> VerificationNeedsStepUpAsync(Guid paymentId, CancellationToken ct = default);

    Task<VerifiedCreditPayment> VerifyAsync(Guid paymentId, VerifyCreditPaymentRequest request, CancellationToken ct = default);

    Task<CreditPaymentDto> RejectAsync(Guid paymentId, RejectCreditPaymentRequest request, CancellationToken ct = default);
}
