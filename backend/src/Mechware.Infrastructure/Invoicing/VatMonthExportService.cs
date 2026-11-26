using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Domain.Payments;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Configurations;

namespace Mechware.Infrastructure.Invoicing;

/// <summary>
/// ADR-0012 §13 (E6.7, threat model IT12, IT17): the monthly Mushak-9.1 working file. Period = the Dhaka calendar month of the issue
/// instant (<see cref="VatMonth"/>), Live series only: a specimen never appears. One REPEATABLE READ snapshot, no row lock; the only
/// write is the audit row of a CSV.
/// </summary>
public sealed class VatMonthExportService(MechwareDbContext db, ICurrentUserService user, InvoicingOptions options, TimeProvider clock) : IVatMonthExportService
{
    public const string ExportedAction = "invoice.exported";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<VatMonthReport> PreviewAsync(string? month, CancellationToken ct = default) =>
        await ReadSnapshot.RunAsync(db, () => BuildAsync(Parse(month), maskBin: true, ct), ct);

    public async Task<VatMonthCsvExport> CsvAsync(string? month, CancellationToken ct = default)
    {
        var actor = user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        var report = await ReadSnapshot.RunAsync(db, () => BuildAsync(Parse(month), maskBin: false, ct), ct);
        var csv = VatMonthCsv.Write(report);
        var t = report.Totals;
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, user.IpAddress, ExportedAction, "invoices", report.Month, null, JsonSerializer.Serialize(new
        {
            month = report.Month,
            rows = csv.Rows,
            sales = t.Sales,
            creditNotes = t.CreditNotes,
            salesNetMinor = t.SalesNetMinor,
            salesVatMinor = t.SalesVatMinor,
            creditNetMinor = t.CreditNetMinor,
            creditVatMinor = t.CreditVatMinor
        }, Json), user.CorrelationId, clock.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(ct);
        return csv;
    }

    private static VatMonth Parse(string? month) =>
        VatMonth.TryParse(month, out var m) ? m : throw new ValidationException("month", "Use a Dhaka calendar month like 2026-12.");

    private async Task<VatMonthReport> BuildAsync(VatMonth m, bool maskBin, CancellationToken ct)
    {
        var (from, to) = (m.StartUtc, m.EndUtc);
        var month = db.Invoices.AsNoTracking().Where(i => i.Mode == InvoicingMode.Live && i.IssuedAtUtc >= from && i.IssuedAtUtc < to);
        var invoices = await month.OrderBy(i => i.IssuedAtUtc).ThenBy(i => i.Number).ToListAsync(ct);

        var perRate = await (from l in db.InvoiceLines
                             join i in month on l.InvoiceId equals i.Id
                             group l by new { i.Kind, l.VatRate } into g
                             select new { g.Key.Kind, g.Key.VatRate, Net = g.Sum(x => x.NetMinor), Sd = g.Sum(x => x.SdMinor), Vat = g.Sum(x => x.VatMinor) }).ToListAsync(ct);
        var rates = (await (from l in db.InvoiceLines
                            join i in month on l.InvoiceId equals i.Id
                            group l.VatRate by l.InvoiceId into g
                            select new { Id = g.Key, Low = g.Min(), High = g.Max() }).ToListAsync(ct))
            .ToDictionary(r => r.Id, r => r.Low == r.High ? VatMonth.Band(r.Low) : "mixed");

        var originalIds = invoices.Where(i => i.OriginalInvoiceId is not null).Select(i => i.OriginalInvoiceId!.Value).Distinct().ToList();
        var originals = await db.Invoices.AsNoTracking().Where(i => originalIds.Contains(i.Id))
            .Select(i => new { i.Id, i.Number, i.IssuedAtUtc }).ToDictionaryAsync(i => i.Id, ct);

        string? Bin(Invoice i) => maskBin ? InvoiceReadService.MaskBin(i.Buyer.Bin) : i.Buyer.Bin;
        var sales = invoices.Where(i => i.Kind == InvoiceKind.TaxInvoice).Select(i => new VatMonthSaleRow(i.Id, i.Number, i.IssuedAtUtc, Dhaka(i.IssuedAtUtc),
            i.OrderNumber, i.Buyer.Name, Bin(i), i.NetMinor, i.SdMinor, i.VatMinor, i.TotalMinor, rates.GetValueOrDefault(i.Id, "none"))).ToList();
        var credits = invoices.Where(i => i.Kind == InvoiceKind.CreditNote).Select(i =>
        {
            var original = originals[i.OriginalInvoiceId!.Value];
            return new VatMonthCreditNoteRow(i.Id, i.Number, i.IssuedAtUtc, Dhaka(i.IssuedAtUtc), original.Number, VatMonth.Of(original.IssuedAtUtc).Label, i.Buyer.Name,
                Bin(i), i.NetMinor, i.SdMinor, i.VatMinor, i.TotalMinor, rates.GetValueOrDefault(i.Id, "none"));
        }).ToList();

        var rateTotals = perRate.GroupBy(r => r.VatRate).OrderByDescending(g => g.Key).Select(g =>
        {
            var (s, c) = (g.Where(r => r.Kind == InvoiceKind.TaxInvoice).ToList(), g.Where(r => r.Kind == InvoiceKind.CreditNote).ToList());
            return new VatMonthRateTotal(VatMonth.Band(g.Key), g.Key, s.Sum(r => r.Net), s.Sum(r => r.Sd), s.Sum(r => r.Vat), c.Sum(r => r.Net), c.Sum(r => r.Sd), c.Sum(r => r.Vat));
        }).ToList();

        var threshold = options.Mushak610ThresholdMinor;
        var candidates = invoices.Where(i => i.TotalMinor > threshold).Select(i => new VatMonthMushak610Row(Wire(i.Kind), i.Number, Dhaka(i.IssuedAtUtc), i.Buyer.Name,
            Bin(i), i.TotalMinor)).ToList();

        var totals = new VatMonthTotals(sales.Count, sales.Sum(r => r.NetMinor), sales.Sum(r => r.SdMinor), sales.Sum(r => r.VatMinor), sales.Sum(r => r.TotalMinor),
            credits.Count, credits.Sum(r => r.NetMinor), credits.Sum(r => r.SdMinor), credits.Sum(r => r.VatMinor), credits.Sum(r => r.TotalMinor));

        return new VatMonthReport(m.Label, from, to, sales, credits, WithholdingsAvailable: false, rateTotals, threshold, candidates,
            await PaidNotShippedAsync(from, to, ct), totals, options.ConsultantChecked);
    }

    /// <summary>(f) Gateway money captured in the month for an order that is not cancelled and had no shipment before the month's end.</summary>
    private async Task<List<VatMonthPaidNotShippedRow>> PaidNotShippedAsync(DateTime start, DateTime end, CancellationToken ct)
    {
        var rows = await (from p in db.Payments.AsNoTracking()
                          join o in db.Orders.AsNoTracking() on p.OrderId equals o.Id
                          where p.Status == PaymentStatus.Captured && p.CapturedAtUtc >= start && p.CapturedAtUtc < end && o.Status != OrderStatus.Cancelled
                                && !db.Set<Shipment>().Any(s => s.OrderId == o.Id && s.ShippedAtUtc < end)
                          orderby p.CapturedAtUtc, o.OrderNumber
                          select new { o.Id, o.OrderNumber, o.Status, PaidAt = p.CapturedAtUtc!.Value, p.AmountMinor }).ToListAsync(ct);
        return [.. rows.Select(r => new VatMonthPaidNotShippedRow(r.Id, r.OrderNumber, Wire(r.Status), Dhaka(r.PaidAt), r.AmountMinor))];
    }

    /// <summary>ISO 8601 with the Dhaka offset, so a spreadsheet sorts it and nobody reads UTC as local time.</summary>
    internal static string Dhaka(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(FiscalYear.DhakaOffset).ToString("yyyy-MM-dd'T'HH:mm:ss'+06:00'", CultureInfo.InvariantCulture);

    private static string Wire<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToSnake(value.ToString());
}

/// <summary>
/// The CSV of <see cref="VatMonthReport"/>: one file, sections a-f one after another, each with its own header row. The finance CSV rules
/// (<see cref="CreditCsv"/>): UTF-8 with BOM, every cell quoted, a text cell starting with <c>= + - @</c>, tab or CR gets a leading <c>'</c>;
/// amounts in poisha and BDT that we format ourselves.
/// </summary>
public static class VatMonthCsv
{
    public static VatMonthCsvExport Write(VatMonthReport r)
    {
        var rows = new List<string>
        {
            Row("mushak_9_1_working_file", r.Month, "Dhaka calendar month, Live series only",
                r.ConsultantChecked ? "consultant checked" : "consultant check before relying on it"),
            Row("from_utc", Utc(r.FromUtc), "to_utc", Utc(r.ToUtc)),
            Row(),
            Row("a", "sales register: Mushak-6.3 tax invoices"),
            Row("number", "issued_at_dhaka", "order_number", "buyer_name", "buyer_bin", "net_minor", "net_bdt", "sd_minor", "sd_bdt", "vat_minor", "vat_bdt",
                "total_minor", "total_bdt", "vat_band")
        };
        rows.AddRange(r.Sales.Select(s => CreditCsv.Row(T(s.Number), T(s.IssuedAtDhaka), T(s.OrderNumber), T(s.BuyerName), T(s.BuyerBin), Money(s.NetMinor),
            Money(s.SdMinor), Money(s.VatMinor), Money(s.TotalMinor), T(s.VatBand))));
        var t = r.Totals;
        rows.Add(CreditCsv.Row(T("total"), T(t.Sales.ToString(CultureInfo.InvariantCulture)), T(""), T(""), T(""), Money(t.SalesNetMinor), Money(t.SalesSdMinor),
            Money(t.SalesVatMinor), Money(t.SalesTotalMinor), T("")));

        rows.AddRange([Row(), Row("b", "credit notes: Mushak-6.7"),
            Row("number", "issued_at_dhaka", "original_number", "original_month", "buyer_name", "buyer_bin", "net_minor", "net_bdt", "sd_minor", "sd_bdt",
                "vat_minor", "vat_bdt", "total_minor", "total_bdt", "vat_band")]);
        rows.AddRange(r.CreditNotes.Select(c => CreditCsv.Row(T(c.Number), T(c.IssuedAtDhaka), T(c.OriginalNumber), T(c.OriginalMonth), T(c.BuyerName), T(c.BuyerBin),
            Money(c.NetMinor), Money(c.SdMinor), Money(c.VatMinor), Money(c.TotalMinor), T(c.VatBand))));
        rows.Add(CreditCsv.Row(T("total"), T(t.CreditNotes.ToString(CultureInfo.InvariantCulture)), T(""), T(""), T(""), T(""), Money(t.CreditNetMinor),
            Money(t.CreditSdMinor), Money(t.CreditVatMinor), Money(t.CreditTotalMinor), T("")));

        rows.AddRange([Row(), Row("c", "verified VDS certificates", r.WithholdingsAvailable ? "" : "not recorded yet (E6.6)"), Row()]);

        rows.AddRange([Row("d", "totals per VAT rate"),
            Row("band", "vat_rate", "sales_net_minor", "sales_net_bdt", "sales_sd_minor", "sales_sd_bdt", "sales_vat_minor", "sales_vat_bdt", "credit_net_minor",
                "credit_net_bdt", "credit_sd_minor", "credit_sd_bdt", "credit_vat_minor", "credit_vat_bdt", "net_vat_minor", "net_vat_bdt")]);
        rows.AddRange(r.RateTotals.Select(d => CreditCsv.Row(T(d.Band), T(d.VatRate.ToString("0.####", CultureInfo.InvariantCulture)), Money(d.SalesNetMinor),
            Money(d.SalesSdMinor), Money(d.SalesVatMinor), Money(d.CreditNetMinor), Money(d.CreditSdMinor), Money(d.CreditVatMinor), Money(d.SalesVatMinor - d.CreditVatMinor))));
        rows.Add(CreditCsv.Row(T("total"), T(""), Money(t.SalesNetMinor), Money(t.SalesSdMinor), Money(t.SalesVatMinor), Money(t.CreditNetMinor), Money(t.CreditSdMinor),
            Money(t.CreditVatMinor), Money(t.NetVatMinor)));

        rows.AddRange([Row(), Row("e", "Mushak-6.10 candidates: a single document above the threshold", "threshold_minor", r.Mushak610ThresholdMinor.ToString(CultureInfo.InvariantCulture)),
            Row("kind", "number", "issued_at_dhaka", "buyer_name", "buyer_bin", "total_minor", "total_bdt")]);
        rows.AddRange(r.Mushak610Candidates.Select(e => CreditCsv.Row(T(e.Kind), T(e.Number), T(e.IssuedAtDhaka), T(e.BuyerName), T(e.BuyerBin), Money(e.TotalMinor))));

        rows.AddRange([Row(), Row("f", "check list: money received in the month, not shipped by the month's end"),
            Row("order_number", "status", "paid_at_dhaka", "paid_minor", "paid_bdt")]);
        rows.AddRange(r.PaidNotShipped.Select(f => CreditCsv.Row(T(f.OrderNumber), T(f.Status), T(f.PaidAtDhaka), Money(f.PaidMinor))));

        var dataRows = r.Sales.Count + r.CreditNotes.Count + r.RateTotals.Count + r.Mushak610Candidates.Count + r.PaidNotShipped.Count;
        return new VatMonthCsvExport("mushak-9_1-" + r.Month + ".csv", CreditCsv.Document(rows), dataRows);
    }

    private static string T(string? text) => CreditCsv.Text(text);

    private static string Row(params string[] cells) => CreditCsv.Row([.. cells.Select(CreditCsv.Text)]);

    private static string Money(long poisha) => CreditCsv.Minor(poisha) + "," + CreditCsv.Bdt(poisha);

    private static string Utc(DateTime utc) => utc.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
