namespace Mechware.Application.Credit;

public enum CreditReductionKind { OrderCancelled = 1, CreditNote = 2 }

/// <summary>
/// One reduction of an order's charge (ADR-0010 §8.2). <paramref name="AmountMinor"/> null = everything not yet reduced (a
/// cancellation); a credit note gives its amount and <paramref name="CreditNoteId"/>. <paramref name="SourceKey"/> names the
/// business event once (<c>cancel:{orderId:N}</c>, <c>creditnote:{id:N}</c>; 1-60 printable ASCII): the same key is applied once.
/// </summary>
public sealed record CreditReduction(Guid OrderId, CreditReductionKind Kind, long? AmountMinor, string SourceKey, Guid? CreditNoteId,
    Guid? ActorUserId, string Reason);

/// <summary>What the order owes less, what moved on account (money already paid), and whether this key had run before.</summary>
public sealed record CreditReductionResult(long ReceivableReleasedMinor, long MovedOnAccountMinor, bool AlreadyApplied);

/// <summary>The credit ledger as other modules use it (E7.2 cancel, E6.5 credit notes).</summary>
public interface ICreditLedger
{
    /// <summary>
    /// Runs INSIDE the caller's open transaction, after the caller locked the order row with <c>FOR NO KEY UPDATE</c> (never
    /// <c>FOR UPDATE</c>: a verification or bounce holding the company lock inserts ledger lines naming the order, and their foreign
    /// key takes <c>FOR KEY SHARE</c> on it, so <c>FOR UPDATE</c> deadlocks with them, CS16). Takes the <c>companies</c> row lock
    /// itself (lock order orders → companies → promotions) and saves its lines and one audit row in that transaction. Returns null for an
    /// order that was never charged (not on credit, or cancelled while waiting for approval). 409
    /// <c>CREDIT_REDUCTION_EXCEEDS_CHARGE</c>, <c>CREDIT_REDUCTION_CONFLICT</c>; throws without an open transaction.
    /// </summary>
    Task<CreditReductionResult?> ReduceChargeAsync(CreditReduction request, CancellationToken ct = default);
}
