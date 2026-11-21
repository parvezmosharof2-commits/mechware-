using Mechware.Domain.Invoicing;

namespace Mechware.Application.Invoicing;

/// <summary>A serial taken inside the issuing transaction, with the database issue instant (UTC) it was taken at.</summary>
public sealed record TakenInvoiceNumber(Guid SellerId, InvoiceSeries Series, int FiscalYear, long Serial, DateTime IssuedAtUtc)
{
    public string Format(string livePrefix) => InvoiceNumber.Format(livePrefix, Series, FiscalYear, Serial);
}

/// <summary>
/// ADR-0012 §4 and §15: takes the next gap-free serial of a series. It runs inside the caller's transaction and never opens one,
/// so a rollback of the issue gives the serial back; the counter row is the LAST lock of that transaction (take the number after
/// every other lock, then only insert and commit). Throws <see cref="InvalidOperationException"/> outside a transaction and
/// <see cref="InvoiceClockBehindException"/> when the database clock is behind the series' last issue.
/// </summary>
public interface IInvoiceNumberAllocator
{
    Task<TakenInvoiceNumber> TakeAsync(Guid sellerId, InvoiceSeries series, CancellationToken ct = default);
}

/// <summary>
/// The database clock went backwards past the last issue of a series (ADR-0012 §4 step 4, threat model IT11). Not a
/// <c>DomainException</c> on purpose: nothing the caller sent is wrong, so it answers 500 and is logged as an alert.
/// </summary>
public sealed class InvoiceClockBehindException(InvoiceSeries series, DateTime issuedAtUtc, DateTime? lastIssuedAtUtc)
    : Exception($"The database clock ({issuedAtUtc:O}) is behind the last {series} issue ({lastIssuedAtUtc:O}); no number is taken.")
{
    public const string Code = "INVOICE_CLOCK_BEHIND";
}
