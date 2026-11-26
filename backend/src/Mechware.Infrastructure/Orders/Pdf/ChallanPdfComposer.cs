using Mechware.Application.Pdf;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Pdf;
using static Mechware.Infrastructure.Pdf.PdfText;

namespace Mechware.Infrastructure.Orders.Pdf;

/// <summary>One goods line of a challan: what and how many, never a price.</summary>
internal sealed record ChallanLine(string? Sku, string Description, int Quantity);

/// <summary>Everything a challan prints. There is deliberately no money field in it, so no price can reach the page.</summary>
internal sealed record ChallanData(
    string ChallanNumber, string OrderNumber, string? PurchaseOrderNumber, DateOnly DispatchDate, string BuyerName, string? RecipientName, string? DeliveryAddress,
    string CourierName, string TrackingNumber, IReadOnlyList<ChallanLine> Lines);

/// <summary>
/// Lays out the delivery challan (owner's format folder, 2026-11-26): challan no., ref PO, date (Dhaka), To, subject, SL / description / qty with unit
/// (no prices), courier and tracking, a "received by" line and the signatory. Same letterhead band, table style and no-wrap rule as the invoice
/// (E6.4c): the SL and Qty columns are measured so a number never wraps, and Description takes the rest. Plain text only.
/// </summary>
internal static class ChallanPdfComposer
{
    private const float Margin = 48, CellPad = 4;

    /// <summary><c>MW-CH-</c> + the order number without its own <c>MW-</c> (an order is <c>MW-yymmdd-XXXXXX</c>, so the challan is <c>MW-CH-yymmdd-XXXXXX</c>).</summary>
    public static string ChallanNumber(string orderNumber, string prefix = "MW") =>
        $"{prefix}-CH-" + (orderNumber.StartsWith(prefix + "-", StringComparison.Ordinal) ? orderNumber[(prefix.Length + 1)..] : orderNumber);

    public static PdfDocumentModel Compose(ChallanData d, string locale, InvoicingOptions options)
    {
        bool bn = locale == "bn";
        string L(string en, string bangla) => bn ? bangla : en;
        static string? Clean(string? v) => string.IsNullOrWhiteSpace(v) ? null : v.Trim();
        string company = (bn ? Clean(options.LegalNameBn) : null) ?? Clean(options.LegalNameEn) ?? "MECHWARE";
        string address = (bn ? Clean(options.RegisteredAddressBn) : null) ?? Clean(options.RegisteredAddressEn) ?? "";
        var blocks = new List<PdfBlock>
        {
            new PdfBand(company, address.Length > 0 ? address : null, [])
        };
        blocks.Add(new PdfHeading(L("Delivery challan", "ডেলিভারি চালান")));
        var box = new List<KeyValuePair<string, string>>
        {
            new(L("Challan no.", "চালান নং"), Digits(d.ChallanNumber, bn)),
            new(L("Date (Dhaka)", "তারিখ (ঢাকা)"), Date(d.DispatchDate, bn)),
            new(L("Order no.", "অর্ডার নং"), Digits(d.OrderNumber, bn)),
        };
        if (Clean(d.PurchaseOrderNumber) is { } po) box.Add(new(L("Ref. PO", "রেফ. পিও"), po));
        box.Add(new(L("Courier", "কুরিয়ার"), d.CourierName + " · " + d.TrackingNumber));
        blocks.Add(new PdfKeyValues(box));

        var to = new List<string> { d.BuyerName };
        if (Clean(d.RecipientName) is { } rn && rn != d.BuyerName) to.Add(L("Attn: ", "মনোযোগ: ") + rn);
        if (Clean(d.DeliveryAddress) is { } addr) to.Add(addr);
        blocks.Add(new PdfParagraph(L("To:\n", "প্রতি:\n") + string.Join("\n", to)));
        blocks.Add(new PdfParagraph(L("Subject: Delivery of goods against order ", "বিষয়: অর্ডার নং ") + d.OrderNumber + L("", " এর বিপরীতে পণ্য সরবরাহ")));

        var header = new List<string> { L("SL", "নং"), L("Description", "বিবরণ"), L("Qty", "পরিমাণ") };
        var rows = d.Lines.Select((l, i) => (IReadOnlyList<string>)new List<string>
        {
            Int(i + 1, bn), (string.IsNullOrWhiteSpace(l.Sku) ? "" : l.Sku + "  ") + l.Description, Int(l.Quantity, bn) + " " + L("pcs", "পিস")
        }).ToList();
        blocks.Add(new PdfTable(header, rows, MoneyColumn: 2, ColumnWeights: Weights(header, rows), Branded: true));
        blocks.Add(new PdfParagraph("\n" + L("Total items: ", "মোট আইটেম: ") + Int(d.Lines.Count, bn) + " · " + L("Total quantity: ", "মোট পরিমাণ: ") + Int(d.Lines.Sum(l => (long)l.Quantity), bn)));

        string signName = Clean(options.Signatory?.Name) ?? Clean(options.ResponsiblePersonName) ?? "";
        string signRole = Clean(options.Signatory?.Name) is null ? Clean(options.ResponsiblePersonDesignation) ?? "" : Clean(options.Signatory?.Designation) ?? "";
        var receive = L("Goods received in good order and condition.\n\n\nReceived by (name, signature, date): ______________________________",
            "পণ্য ভালো অবস্থায় বুঝে পাওয়া গেল।\n\n\nগ্রহণকারী (নাম, স্বাক্ষর, তারিখ): ______________________________");
        var from = L("For ", "পক্ষে ") + company + "\n\n\n" + string.Join(", ", new[] { signName, signRole }.Where(x => x.Length > 0));
        // One paragraph, so the receiving line and the signatory never split across a page.
        blocks.Add(new PdfParagraph(receive + "\n\n" + from));

        return new PdfDocumentModel($"{L("Delivery challan", "ডেলিভারি চালান")} {d.ChallanNumber}", locale, blocks, Digits(d.ChallanNumber, bn),
            [L("This challan lists the goods delivered; it is not a tax invoice and shows no prices.", "এই চালানে সরবরাহকৃত পণ্যের তালিকা আছে; এটি কর চালান নয় এবং এতে কোনো মূল্য নেই।")]);
    }

    /// <summary>SL and Qty are exactly as wide as their widest cell; Description takes the rest, so no number ever wraps.</summary>
    private static double[] Weights(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        const float size = 10f;
        var w = new double[header.Count];
        foreach (var c in new[] { 0, 2 })
        {
            var widest = SkiaPdfRenderer.MeasureWidth(header[c], true, size);
            foreach (var r in rows) widest = Math.Max(widest, SkiaPdfRenderer.MeasureWidth(r[c], false, size));
            w[c] = Math.Ceiling(widest) + 2 * CellPad + 2;
        }

        w[1] = Math.Max(595 - 2 * Margin - w[0] - w[2], 1);
        return w;
    }

    public static PdfResult Render(IPdfRenderer renderer, ChallanData data, string locale, InvoicingOptions options) => renderer.Render(Compose(data, locale, options));
}
