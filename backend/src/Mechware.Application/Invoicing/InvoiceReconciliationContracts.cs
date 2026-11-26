namespace Mechware.Application.Invoicing;

/// <summary>
/// One mismatch of the nightly invoice check (ADR-0012 §17). <paramref name="Check"/> is one of the
/// <c>InvoiceReconciliationService</c> check names; <paramref name="EntityType"/>/<paramref name="EntityId"/> name the row it is about
/// (an invoice, a sub-order or a seller's series); <paramref name="Detail"/> holds no personal data (series and fiscal year, or an order id).
/// </summary>
public sealed record InvoiceReconciliationFinding(string Check, string EntityType, Guid EntityId, string Detail, long Expected, long Actual);

/// <summary>
/// One pass. <paramref name="Ran"/> is false when another pass held the lock (nothing was read or written). <paramref name="Findings"/>:
/// every mismatch found now, also those recorded earlier the same Dhaka day; <paramref name="NewlyRecorded"/>: how many got their audit row
/// and outbox event in this pass.
/// </summary>
public sealed record InvoiceReconciliationReport(DateOnly RunOn, bool Ran, int InvoicesChecked, IReadOnlyList<InvoiceReconciliationFinding> Findings,
    int NewlyRecorded);

/// <summary>
/// The nightly invoice reconciliation (E6.3d, ADR-0012 §8, §17, threat model IT13, IT16; B3.10 pattern). Reads every invoice, counter,
/// shipment and sub-order in ONE <c>REPEATABLE READ</c> snapshot without a row lock, under a transaction-scoped advisory lock so only one
/// pass runs at a time, and records each finding once per Dhaka day (audit <c>invoice.reconciliation_finding</c>, system actor, + outbox
/// event of the same name + an ALERT log). It never changes an invoice, a line, a counter, a shipment or an order: a wrong invoice is
/// answered by a credit note (§9), damage below the application by the runbook.
/// </summary>
public interface IInvoiceReconciliationService
{
    Task<InvoiceReconciliationReport> RunAsync(DateTime nowUtc, CancellationToken ct = default);
}
