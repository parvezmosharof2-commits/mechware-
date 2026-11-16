using Mechware.Domain.Credit;

namespace Mechware.Application.Credit;

/// <summary>A payment the company reported that finance has not verified yet: when, how, how much, and the end of the reference. No staff, no note, no id.</summary>
public sealed record BuyerPendingPaymentDto(DateOnly ReceivedOn, string Method, long AmountMinor, string ReferenceTail);

/// <summary>
/// The company's own credit position (ADR-0010 §10): terms, limit, used, available (never below 0), on-account money, what is overdue,
/// the oldest due date, <paramref name="OnHold"/> (new credit orders are refused; no override or staff detail), the aging and pending payments.
/// </summary>
public sealed record BuyerCreditDto(Guid CompanyId, string Currency, DateOnly AsOf, string Terms, long LimitMinor, long UsedMinor, long AvailableMinor,
    long OnAccountMinor, long OverdueMinor, DateOnly? OldestDueOn, bool OnHold, CreditAgingBuckets Aging, IReadOnlyList<BuyerPendingPaymentDto> PendingPayments);

/// <summary><paramref name="Status"/> is <c>open</c>, <c>partially_paid</c> or <c>overdue</c>; <paramref name="DaysPastDue"/> is 0 until the due date has passed.</summary>
public sealed record BuyerOpenItemDto(string OrderNumber, long ChargeMinor, long ReceivableMinor, DateOnly DueOn, int DaysPastDue, string Status);

/// <summary>One statement line: no ids of staff, adjustments or payments, no reason, no actor, only the end of a payment reference.</summary>
public sealed record BuyerStatementLineDto(long Seq, DateOnly OccurredOn, string Kind, string? OrderNumber, string? PaymentMethod, string? ReferenceTail,
    long ReceivableDeltaMinor, long OnAccountDeltaMinor, long ReceivableAfterMinor, long OnAccountAfterMinor, DateOnly? DueOn);

/// <summary>The company's ledger lines in posting order, with the current totals read in the same snapshot.</summary>
public sealed record BuyerStatementDto(Guid CompanyId, DateOnly? From, DateOnly? To, long CurrentReceivableMinor, long CurrentOnAccountMinor,
    CreditPage<BuyerStatementLineDto> Lines);

/// <summary>
/// The buyer's reads of their own company's credit (B3.9). Every method takes the signed-in person and re-checks the membership in the
/// database: an unknown company, a non-member, an unaccepted or deactivated member, a member without a role in
/// <c>Credit:BalanceVisibleTo</c> and a Pending or Rejected company all throw the same <see cref="KeyNotFoundException"/> ("Company not found.").
/// A Suspended company still sees its credit. READ-ONLY: one REPEATABLE READ snapshot, no lock, no ledger line.
/// </summary>
public interface IBuyerCreditService
{
    Task<BuyerCreditDto> GetAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default);

    /// <summary>Orders that still owe something, oldest due date first; at most 100 a page.</summary>
    Task<CreditPage<BuyerOpenItemDto>> OpenItemsAsync(Guid actorUserId, Guid companyId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>400 when <paramref name="from"/> is after <paramref name="to"/>.</summary>
    Task<BuyerStatementDto> StatementAsync(Guid actorUserId, Guid companyId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct = default);
}
