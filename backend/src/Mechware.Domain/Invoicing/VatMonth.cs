using System.Globalization;

namespace Mechware.Domain.Invoicing;

/// <summary>
/// A Mushak-9.1 return month (ADR-0012 §5, §13): a Dhaka calendar month, as the UTC range [day 1 00:00 +06:00, next day 1 00:00 +06:00).
/// An invoice issued at 2026-11-30 18:30 UTC is 1 December in Dhaka, so it belongs to December, never to November.
/// </summary>
public readonly record struct VatMonth
{
    private VatMonth(int year, int month) => (Year, Month) = (year, month);

    public int Year { get; }
    public int Month { get; }

    public DateTime StartUtc => DateTime.SpecifyKind(new DateTime(Year, Month, 1) - FiscalYear.DhakaOffset, DateTimeKind.Utc);
    public DateTime EndUtc => (Month == 12 ? new VatMonth(Year + 1, 1) : new VatMonth(Year, Month + 1)).StartUtc;
    public string Label => string.Create(CultureInfo.InvariantCulture, $"{Year:D4}-{Month:D2}");

    /// <summary>The return month of an issue instant (its Dhaka calendar month).</summary>
    public static VatMonth Of(DateTime utc)
    {
        var (year, month) = InvoiceDates.ReturnMonth(utc);
        return new VatMonth(year, month);
    }

    /// <summary><c>yyyy-MM</c>, years 2000-2999 only.</summary>
    public static bool TryParse(string? text, out VatMonth month)
    {
        month = default;
        if (text is null || !DateTime.TryParseExact(text.Trim(), "yyyy-MM", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) || d.Year is < 2000 or > 2999)
            return false;
        month = new VatMonth(d.Year, d.Month);
        return true;
    }

    /// <summary>
    /// The 9.1 rate band of a line's VAT rate: 15 % standard, below it reduced, 0 % "zero_or_exempt" (an order line carries no exemption
    /// flag, so zero-rated and exempt cannot be told apart yet; consultant question), above 15 % "other".
    /// </summary>
    public static string Band(decimal vatRate) => vatRate switch
    {
        0m => "zero_or_exempt",
        StandardVatRate => "standard",
        < StandardVatRate => "reduced",
        _ => "other"
    };

    public const decimal StandardVatRate = 0.15m;
}
