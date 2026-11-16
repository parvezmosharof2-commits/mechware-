using System.Text.Json.Serialization;

namespace Mechware.Application.Credit;

/// <summary>
/// <c>PUT admin/companies/{companyId}/credit/hold-override</c> (ADR-0010 §9, B3.7): lift the overdue hold for <c>days</c> from now
/// (1 to <c>Credit:HoldOverrideMaxDays</c>, at most 7), or end a lift with 0; a reason of 5-500 characters. <c>days</c> is a
/// required JSON integer (CS10): a missing value never ends a lift by accident.
/// </summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record OverrideCreditHoldRequest(int? Days, string? Reason);

/// <summary>
/// Staff view of a company's hold. <paramref name="OverdueSince"/>: the oldest due date of a charge that still owes and is more than
/// the hold days past due (null = none). <paramref name="OnHold"/>: overdue and not lifted now. <paramref name="HoldNoticedAtUtc"/>:
/// when the sweep first saw the company overdue (null = not at its last pass).
/// </summary>
public sealed record CreditHoldDto(Guid CompanyId, DateOnly? OverdueSince, bool OnHold, DateTime? OverrideUntilUtc, DateTime? HoldNoticedAtUtc);

/// <summary>
/// The overdue credit hold (ADR-0010 §9, threat model CS25). Placement and approval-confirmation check the ledger themselves under the
/// company lock (<c>OrderService</c>); this service lifts the hold (Admin, step-up, reason, audited) and notes holds starting and ending
/// for the outbox (reminders, F3). The hold never blocks payments, gateway-paid orders or statements.
/// </summary>
public interface ICreditHoldService
{
    /// <summary>404 unless the company is Active or Suspended; 400 for days outside 0..max or a reason outside 5-500.</summary>
    Task<CreditHoldDto> OverrideAsync(Guid companyId, OverrideCreditHoldRequest request, CancellationToken ct = default);

    /// <summary>
    /// One pass of the Worker sweep: every company whose noted hold differs from the ledger is decided again under its row lock and
    /// gets one audit row and one outbox event (<c>credit.hold_started</c> | <c>credit.hold_ended</c>). Idempotent: a second pass, or
    /// one running in parallel, changes nothing. Returns how many companies changed.
    /// </summary>
    Task<int> SweepAsync(DateTime nowUtc, int batchSize = 200, CancellationToken ct = default);
}
