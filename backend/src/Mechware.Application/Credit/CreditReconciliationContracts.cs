using Mechware.Domain.Credit;

namespace Mechware.Application.Credit;

/// <summary>
/// One reconciliation pass. <paramref name="Findings"/>: every mismatch found now (also those already recorded earlier the same day);
/// <paramref name="NewlyRecorded"/>: how many got their audit row and outbox event in this pass. <paramref name="PaymentsWaitingOver7Days"/>
/// and <paramref name="PostDatedChequesDue"/> are the ADR's work lists (recorded, not verified): not mismatches.
/// </summary>
public sealed record CreditReconciliationReport(DateOnly RunOn, int CompaniesChecked, IReadOnlyList<CreditReconciliationFinding> Findings,
    int NewlyRecorded, int PaymentsWaitingOver7Days, int PostDatedChequesDue);

/// <summary>
/// The nightly credit reconciliation (ADR-0010 §11, threat model CS28). Reads every company's books in REPEATABLE READ snapshots without
/// any row lock, checks them (<see cref="CreditReconciliation"/>) and records each mismatch once per Dhaka day: an audit row
/// <c>credit.reconciliation_mismatch</c> (system actor) and an outbox event of the same name, for alerting. It never changes a ledger
/// line, a company, a payment or an adjustment: corrections go through adjustments (§8.3).
/// </summary>
public interface ICreditReconciliationService
{
    Task<CreditReconciliationReport> RunAsync(DateTime nowUtc, CancellationToken ct = default);
}
