namespace Mechware.Domain.Invoicing;

/// <summary>
/// The gap-free counter of one series (ADR-0012 §4, table <c>invoice_number_counters</c>): one row per (seller, series) carrying
/// the current fiscal year, the next serial and the last issue instant. A new row is year 0, next 1. Never a database sequence:
/// <c>nextval()</c> is not given back on ROLLBACK, so every failed issue would leave a hole. The row is read under
/// <c>FOR UPDATE</c> inside the issuing transaction and changed by <see cref="Take"/>; the database guard
/// (<c>InvoiceNumberingSql</c>) repeats the rule, so a hand-written UPDATE cannot skip or reuse a serial either.
/// </summary>
public sealed class InvoiceNumberCounter
{
    public const int NoYear = 0;

    public Guid SellerId { get; private set; }
    public InvoiceSeries Series { get; private set; }

    /// <summary>Start year of the fiscal year of the last issue; <see cref="NoYear"/> before the first.</summary>
    public int Year { get; private set; }

    public long NextSerial { get; private set; }
    public DateTime? LastIssuedAtUtc { get; private set; }

    private InvoiceNumberCounter() { }

    public InvoiceNumberCounter(Guid sellerId, InvoiceSeries series, int year = NoYear, long nextSerial = 1, DateTime? lastIssuedAtUtc = null)
    {
        (SellerId, Series, Year, NextSerial, LastIssuedAtUtc) = (sellerId, series, year, nextSerial, lastIssuedAtUtc);
    }

    /// <summary>
    /// Takes the serial for an issue at <paramref name="issuedAtUtc"/> (the database clock read after the row lock, ADR §5):
    /// the next serial of the same fiscal year, or 1 when the Dhaka date has reached a later year. A clock behind the last
    /// issue (an earlier instant or an earlier year) answers null and changes nothing; the caller refuses the issue and alerts.
    /// </summary>
    public (int FiscalYear, long Serial)? Take(DateTime issuedAtUtc)
    {
        var year = FiscalYear.Of(issuedAtUtc);
        if (year < Year || issuedAtUtc < LastIssuedAtUtc) return null;
        var serial = year > Year ? 1 : NextSerial;
        (Year, NextSerial, LastIssuedAtUtc) = (year, serial + 1, issuedAtUtc);
        return (year, serial);
    }
}
