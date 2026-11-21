using System.Globalization;
using System.Text.RegularExpressions;

namespace Mechware.Domain.Invoicing;

/// <summary>
/// A numbering series (ADR-0012 §4): one per issuing seller and kind and mode. Credit notes have their own series, and the
/// specimen ones (staging, demos, the consultant's sample) never share a counter with the live ones. Stored as text.
/// </summary>
public enum InvoiceSeries
{
    TaxInvoice,
    CreditNote,
    SpecimenTaxInvoice,
    SpecimenCreditNote
}

/// <summary>
/// The Bangladesh fiscal year, July to June, decided by the Dhaka calendar date (a fixed +06:00, no daylight saving; ADR-0012
/// §5, threat model IT10): 30 June 23:59:59 Dhaka (17:59:59 UTC) is the old year, 18:00:00 UTC on 30 June is 1 July Dhaka.
/// A year is named by its start year; its label is <c>2026-27</c>.
/// </summary>
public static class FiscalYear
{
    public const int Min = 2000;
    public const int Max = 9998;
    public static readonly TimeSpan DhakaOffset = TimeSpan.FromHours(6);

    public static DateOnly DhakaDate(DateTime utc)
    {
        if (utc.Kind == DateTimeKind.Local)
            throw new ArgumentException("The issue instant is a UTC instant.", nameof(utc));
        return DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(DhakaOffset));
    }

    /// <summary>The start year of the fiscal year the instant falls in.</summary>
    public static int Of(DateTime utc)
    {
        var d = DhakaDate(utc);
        return d.Month >= 7 ? d.Year : d.Year - 1;
    }

    public static string Label(int startYear)
    {
        if (startYear is < Min or > Max) throw new ArgumentOutOfRangeException(nameof(startYear), "Not a fiscal year.");
        return string.Create(CultureInfo.InvariantCulture, $"{startYear:D4}-{(startYear + 1) % 100:D2}");
    }
}

/// <summary>
/// The printed number (ADR-0012 §4): <c>{Prefix}-INV-{FY}-{serial:D6}</c> and <c>{Prefix}-CN-{FY}-{serial:D6}</c>, e.g.
/// <c>MW-INV-2026-27-000482</c>; specimen series always print <c>SPEC</c>. No slashes, so a number is safe in file and URL
/// names. Above 999,999 the serial simply gets longer.
/// </summary>
public static partial class InvoiceNumber
{
    public const string SpecimenPrefix = "SPEC";

    public static bool IsSpecimen(InvoiceSeries series) => series is InvoiceSeries.SpecimenTaxInvoice or InvoiceSeries.SpecimenCreditNote;

    public static bool IsCreditNote(InvoiceSeries series) => series is InvoiceSeries.CreditNote or InvoiceSeries.SpecimenCreditNote;

    /// <param name="livePrefix">The configured prefix of the live series (<c>Invoicing:NumberPrefix</c>, <c>MW</c>).</param>
    public static string Format(string livePrefix, InvoiceSeries series, int fiscalYear, long serial)
    {
        if (!Enum.IsDefined(series)) throw new ArgumentOutOfRangeException(nameof(series));
        if (livePrefix is null || !PrefixPattern().IsMatch(livePrefix) || livePrefix == SpecimenPrefix)
            throw new ArgumentException("The number prefix is 2 to 6 capital letters and not SPEC.", nameof(livePrefix));
        if (serial < 1) throw new ArgumentOutOfRangeException(nameof(serial), "Serials start at 1.");
        var prefix = IsSpecimen(series) ? SpecimenPrefix : livePrefix;
        var kind = IsCreditNote(series) ? "CN" : "INV";
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}-{kind}-{FiscalYear.Label(fiscalYear)}-{serial:D6}");
    }

    [GeneratedRegex("^[A-Z]{2,6}$")]
    private static partial Regex PrefixPattern();
}
