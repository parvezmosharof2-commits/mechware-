namespace Mechware.Application.Invoicing;

/// <summary>(a) One Live Mushak-6.3 of the month. <c>VatBand</c> is the band of its lines' rate, or "mixed" (section (d) splits per rate).</summary>
public sealed record VatMonthSaleRow(Guid InvoiceId, string Number, DateTime IssuedAtUtc, string IssuedAtDhaka, string OrderNumber, string BuyerName,
    string? BuyerBin, long NetMinor, long SdMinor, long VatMinor, long TotalMinor, string VatBand);

/// <summary>(b) One Live Mushak-6.7 of the month, with the 6.3 it corrects and that invoice's own return month.</summary>
public sealed record VatMonthCreditNoteRow(Guid InvoiceId, string Number, DateTime IssuedAtUtc, string IssuedAtDhaka, string OriginalNumber, string OriginalMonth,
    string BuyerName, string? BuyerBin, long NetMinor, long SdMinor, long VatMinor, long TotalMinor, string VatBand);

/// <summary>(d) The lines of the month per VAT rate: sales (6.3) and credits (6.7) side by side, so a rate's net VAT is <c>SalesVat - CreditVat</c>.</summary>
public sealed record VatMonthRateTotal(string Band, decimal VatRate, long SalesNetMinor, long SalesSdMinor, long SalesVatMinor, long CreditNetMinor,
    long CreditSdMinor, long CreditVatMinor);

/// <summary>(e) A single 6.3 or 6.7 whose total is above <c>Invoicing:Mushak610ThresholdMinor</c>.</summary>
public sealed record VatMonthMushak610Row(string Kind, string Number, string IssuedAtDhaka, string BuyerName, string? BuyerBin, long TotalMinor);

/// <summary>(f) Check list: an order whose gateway money arrived in the month but that had not shipped by the month's end (ADR-0012 §2).</summary>
public sealed record VatMonthPaidNotShippedRow(Guid OrderId, string OrderNumber, string Status, string PaidAtDhaka, long PaidMinor);

public sealed record VatMonthTotals(int Sales, long SalesNetMinor, long SalesSdMinor, long SalesVatMinor, long SalesTotalMinor, int CreditNotes,
    long CreditNetMinor, long CreditSdMinor, long CreditVatMinor, long CreditTotalMinor)
{
    /// <summary>Output VAT of the month after its credit notes.</summary>
    public long NetVatMinor => SalesVatMinor - CreditVatMinor;
}

/// <summary>
/// The 9.1 working file of one Dhaka calendar month (ADR-0012 §13 sections a-f), Live series only. (c) VDS certificates come with E6.6:
/// until then <c>WithholdingsAvailable</c> is false and the section is empty. In the JSON preview the BINs are masked (last four digits).
/// </summary>
public sealed record VatMonthReport(string Month, DateTime FromUtc, DateTime ToUtc, IReadOnlyList<VatMonthSaleRow> Sales,
    IReadOnlyList<VatMonthCreditNoteRow> CreditNotes, bool WithholdingsAvailable, IReadOnlyList<VatMonthRateTotal> RateTotals, long Mushak610ThresholdMinor,
    IReadOnlyList<VatMonthMushak610Row> Mushak610Candidates, IReadOnlyList<VatMonthPaidNotShippedRow> PaidNotShipped, VatMonthTotals Totals, bool ConsultantChecked);

public sealed record VatMonthCsvExport(string FileName, string Content, int Rows);

/// <summary>
/// The monthly Mushak-9.1 export (ADR-0012 §13, threat model IT12, IT17). Reads one REPEATABLE READ snapshot and never locks or changes an
/// invoice. The endpoint asks <c>invoices.export</c>, a per-person rate limit, and for the CSV an authenticator code (<c>invoice.export</c>).
/// </summary>
public interface IVatMonthExportService
{
    /// <summary>The console preview: the same content, BINs masked; not audited. A month that is not <c>yyyy-MM</c> is 400.</summary>
    Task<VatMonthReport> PreviewAsync(string? month, CancellationToken ct = default);

    /// <summary>The CSV with full BINs and every free-text cell formula-neutralised; audited <c>invoice.exported</c> (month, rows, totals).</summary>
    Task<VatMonthCsvExport> CsvAsync(string? month, CancellationToken ct = default);
}
