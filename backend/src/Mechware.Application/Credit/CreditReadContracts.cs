using Mechware.Domain.Credit;

namespace Mechware.Application.Credit;

/// <summary>One page of a staff list (<c>items</c> in the order the endpoint documents; <c>totalCount</c> counts every match).</summary>
public sealed record CreditPage<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

/// <summary>
/// One ledger line as staff see it (never sent to a buyer): the order number and the payment's method and full reference are looked up,
/// the running totals come from the append-only chain itself.
/// </summary>
public sealed record CreditLedgerLineDto(long Seq, Guid EventId, string Kind, DateOnly OccurredOn, DateTime CreatedAtUtc, Guid? OrderId,
    string? OrderNumber, Guid? PaymentId, string? PaymentMethod, string? PaymentReference, Guid? AdjustmentId, Guid? CreditNoteId,
    long ReceivableDeltaMinor, long OnAccountDeltaMinor, long ReceivableAfterMinor, long OnAccountAfterMinor, DateOnly? DueOn, Guid? ActorUserId,
    string? Reason);

/// <summary><paramref name="LateEntry"/>: recorded with a late-entry reason; <paramref name="PostDated"/>: a cheque dated after today (Dhaka), not yet verifiable.</summary>
public sealed record CreditPaymentListItem(CreditPaymentDto Payment, string CompanyName, bool LateEntry, bool PostDated);

/// <summary>One payment with the ledger lines written in its name (applications, on-account, reversals).</summary>
public sealed record CreditPaymentDetail(CreditPaymentDto Payment, string CompanyName, bool LateEntry, bool PostDated, IReadOnlyList<CreditLedgerLineDto> Lines);

public sealed record CreditAdjustmentListItem(CreditAdjustmentDto Adjustment, string CompanyName);

public sealed record CreditAdjustmentDetail(CreditAdjustmentDto Adjustment, string CompanyName, IReadOnlyList<CreditLedgerLineDto> Lines);

/// <summary>
/// A company's open receivables by days past due. <paramref name="OnHold"/> = the overdue hold of B3.7 as of now (an Admin's lift
/// counts); <paramref name="MaxDaysPastDue"/> is 0 when nothing is overdue.
/// </summary>
public sealed record CreditAgingRow(Guid CompanyId, string CompanyName, int OpenOrders, long ReceivableMinor, long OverdueMinor, CreditAgingBuckets Buckets,
    DateOnly OldestDueOn, int MaxDaysPastDue, long OnAccountMinor, bool OnHold, DateTime? HoldOverrideUntilUtc);

/// <summary>
/// The aging report, read in one snapshot. <c>totals</c> and the two amounts cover EVERY matching company, not just the page;
/// <c>asOf</c> is the Dhaka date the buckets were computed for.
/// </summary>
public sealed record CreditAgingReport(DateOnly AsOf, CreditPage<CreditAgingRow> Companies, CreditAgingBuckets Totals, long ReceivableMinor, long OverdueMinor);

/// <summary>
/// A company's ledger lines (filtered by the Dhaka day the line is about, in posting order), with the company's totals read in the SAME
/// snapshot: with no filter, the last line's running totals equal <paramref name="CurrentReceivableMinor"/> and <paramref name="CurrentOnAccountMinor"/>.
/// </summary>
public sealed record CreditStatementDto(Guid CompanyId, string CompanyName, DateOnly? From, DateOnly? To, long CurrentReceivableMinor,
    long CurrentOnAccountMinor, CreditPage<CreditLedgerLineDto> Lines);

/// <summary>A finished export: the file name and the whole text (starts with the byte-order mark), already audited.</summary>
public sealed record CreditCsvExport(string FileName, string Content, int Rows);

/// <summary>
/// Staff reads of the credit ledger and its queues (ADR-0010 §9, §10; B3.8). READ-ONLY: no ledger line, no row lock, every answer
/// from one REPEATABLE READ snapshot. The only write is the audit row of a CSV export.
/// </summary>
public interface ICreditReadService
{
    /// <summary>The verification queue (<paramref name="status"/> recorded, oldest first) or any status (newest first); "all" = every status. 400 on an unknown status.</summary>
    Task<CreditPage<CreditPaymentListItem>> ListPaymentsAsync(string? status, Guid? companyId, int page, int pageSize, CancellationToken ct = default);

    /// <summary>404 for an unknown payment.</summary>
    Task<CreditPaymentDetail> GetPaymentAsync(Guid paymentId, CancellationToken ct = default);

    /// <summary>Adjustments by status (requested, approved, rejected or all), the open ones oldest first.</summary>
    Task<CreditPage<CreditAdjustmentListItem>> ListAdjustmentsAsync(string? status, Guid? companyId, int page, int pageSize, CancellationToken ct = default);

    Task<CreditAdjustmentDetail> GetAdjustmentAsync(Guid adjustmentId, CancellationToken ct = default);

    /// <summary>Companies with an open receivable, the most overdue first; <paramref name="overdueOnly"/> keeps those with something past due.</summary>
    Task<CreditAgingReport> AgingAsync(Guid? companyId, string? q, bool overdueOnly, int page, int pageSize, CancellationToken ct = default);

    /// <summary>404 unless the company is Active or Suspended; 400 when <paramref name="from"/> is after <paramref name="to"/>.</summary>
    Task<CreditStatementDto> StatementAsync(Guid companyId, DateOnly? from, DateOnly? to, int page, int pageSize, CancellationToken ct = default);

    /// <summary>The statement as CSV (at most <c>CreditReadService.MaxCsvRows</c> lines: more is 400, narrow the dates); audited.</summary>
    Task<CreditCsvExport> StatementCsvAsync(Guid companyId, DateOnly? from, DateOnly? to, CancellationToken ct = default);

    /// <summary>The aging of every company as CSV; audited. The endpoint also asks for an authenticator code.</summary>
    Task<CreditCsvExport> AgingCsvAsync(bool overdueOnly, CancellationToken ct = default);
}
