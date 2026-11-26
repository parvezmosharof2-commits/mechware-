using Mechware.Application.Pdf;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Pdf;
using static Mechware.Infrastructure.Pdf.PdfText;

namespace Mechware.Infrastructure.Invoicing.Pdf;

/// <summary>
/// Lays out an issued Mushak-6.3 (tax invoice) or 6.7 (credit note) as a <see cref="PdfDocumentModel"/> (ADR-0012 §12, threat model IT7/IT17).
/// The only input is the stored <see cref="Invoice"/> snapshot: no company, seller, option or clock is read, so a later change never shows on
/// a reprint and two renders of one invoice are byte-identical. Every text goes to the renderer as plain text; nothing is read as markup.
/// 6.7: the domain has no credit-note constructor yet (E6.5a), so the layout follows the shared <see cref="Invoice"/> shape; the original
/// invoice's number is not on the row, so the caller passes it (<paramref name="originalNumber"/>).
/// </summary>
internal static class InvoicePdfComposer
{
    /// <summary>What a missing issuer fact prints: plainly not a number (never a real-looking BIN/TIN).</summary>
    public static string Missing(bool bn) => bn ? "[অনুপস্থিত]" : "[missing]";

    public static PdfDocumentModel Compose(Invoice inv, string locale, string? originalNumber = null)
    {
        ArgumentNullException.ThrowIfNull(inv);
        bool bn = locale == "bn", specimen = inv.Mode == InvoicingMode.Specimen, credit = inv.Kind == InvoiceKind.CreditNote;
        string L(string en, string bangla) => bn ? bangla : en;
        string Fact(string? v) => string.IsNullOrWhiteSpace(v) ? Missing(bn) : v.Trim();
        string Pick(string? en, string? bangla) => bn && !string.IsNullOrWhiteSpace(bangla) ? bangla! : en ?? "";
        var s = inv.Seller;
        var blocks = new List<PdfBlock>();

        blocks.Add(new PdfBand(string.IsNullOrWhiteSpace(Pick(s.LegalNameEn, s.LegalNameBn)) ? Missing(bn) : Pick(s.LegalNameEn, s.LegalNameBn), null,
            ["BIN: " + Fact(s.Bin), "TIN: " + Fact(s.Tin), L("Trade licence: ", "ট্রেড লাইসেন্স: ") + Fact(s.TradeLicence)]));
        if (specimen) blocks.Add(new PdfBand(L("SPECIMEN, NOT A TAX INVOICE", "নমুনা, এটি কর চালান নয়"), null, [], Teal: true));

        blocks.Add(new PdfHeading(credit ? L("Credit note (Mushak-6.7)", "ক্রেডিট নোট (মূসক-৬.৭)") : L("Tax invoice (Mushak-6.3)", "কর চালানপত্র (মূসক-৬.৩)")));
        var box = new List<KeyValuePair<string, string>>
        {
            new(credit ? L("Credit note no.", "ক্রেডিট নোট নং") : L("Invoice no.", "চালান নং"), Digits(inv.Number, bn)),
            new(L("Issue date and time (Dhaka)", "ইস্যুর তারিখ ও সময় (ঢাকা)"), Digits(inv.PrintedIssueTime, bn)),
        };
        if (credit && !string.IsNullOrWhiteSpace(originalNumber)) box.Add(new(L("Against invoice no.", "মূল চালান নং"), Digits(originalNumber!, bn)));
        box.Add(new(L("Order no.", "অর্ডার নং"), inv.OrderNumber));
        if (!string.IsNullOrWhiteSpace(inv.PurchaseOrderNumber)) box.Add(new(L("Buyer PO no.", "ক্রেতার পিও নং"), inv.PurchaseOrderNumber!));
        if (!string.IsNullOrWhiteSpace(inv.QuoteNumber)) box.Add(new(L("Quotation no.", "কোটেশন নং"), inv.QuoteNumber!));
        blocks.Add(new PdfKeyValues(box));

        var seller = new List<string> { Pick(s.LegalNameEn, s.LegalNameBn) is { Length: > 0 } n ? n : Missing(bn) };
        seller.Add(L("Registered address: ", "নিবন্ধিত ঠিকানা: ") + (Pick(s.RegisteredAddressEn, s.RegisteredAddressBn) is { Length: > 0 } ra ? ra : Missing(bn)));
        if (Pick(s.PremisesAddressEn, s.PremisesAddressBn) is { Length: > 0 } pa) seller.Add(L("Premises: ", "ব্যবসাস্থল: ") + pa);
        if (!string.IsNullOrWhiteSpace(s.ResponsiblePersonName)) seller.Add(L("Responsible person: ", "দায়িত্বপ্রাপ্ত ব্যক্তি: ") + s.ResponsiblePersonName + (string.IsNullOrWhiteSpace(s.ResponsiblePersonDesignation) ? "" : ", " + s.ResponsiblePersonDesignation));
        blocks.Add(new PdfParagraph(L("Seller: ", "বিক্রেতা: ") + string.Join("\n", seller)));

        var buyer = new List<string> { inv.Buyer.Name, "BIN: " + (string.IsNullOrWhiteSpace(inv.Buyer.Bin) ? L("not provided", "প্রদান করা হয়নি") : inv.Buyer.Bin!) };
        if (!string.IsNullOrWhiteSpace(inv.Buyer.Address)) buyer.Add(L("Address: ", "ঠিকানা: ") + inv.Buyer.Address);
        blocks.Add(new PdfParagraph(L("Buyer: ", "ক্রেতা: ") + string.Join("\n", buyer)));

        var d = inv.Delivery;
        var to = d.ShipTo is null ? "" : string.Join(", ", new[] { d.ShipTo.SiteName, d.ShipTo.RecipientName, d.ShipTo.OneLine() }.Where(x => !string.IsNullOrWhiteSpace(x)));
        var delivery = new List<string>();
        if (to.Length > 0) delivery.Add(L("Delivered to: ", "ডেলিভারি ঠিকানা: ") + to);
        if (!string.IsNullOrWhiteSpace(d.CourierName)) delivery.Add(L("Vehicle / transport: ", "যানবাহন / পরিবহন: ") + d.CourierName + (string.IsNullOrWhiteSpace(d.TrackingNumber) ? "" : " · " + d.TrackingNumber));
        if (delivery.Count > 0) blocks.Add(new PdfParagraph(string.Join("\n", delivery)));

        // Numeric cells must never wrap: the money columns get the width, the description (which may wrap) the rest. The unit of supply prints on every line,
        // in the quantity cell. A Discount column appears only when some line has one, so qty x unit price - discount = net can always be checked on paper.
        bool discounts = inv.Lines.Any(l => l.DiscountMinor > 0);
        var header = new List<string> { L("No.", "নং"), L("Description", "বিবরণ"), L("Qty", "পরিমাণ"), L("Unit price (excl. VAT)", "একক মূল্য (ভ্যাট বাদে)") };
        if (discounts) header.Add(L("Discount", "ছাড়"));
        header.AddRange([L("Net", "নিট"), L("VAT %", "ভ্যাট %"), L("VAT", "ভ্যাট"), L("Total (incl. VAT)", "মোট (ভ্যাটসহ)")]);
        var rows = inv.Lines.OrderBy(l => l.LineNo).Select(l =>
        {
            var cells = new List<string> { Int(l.LineNo, bn), (string.IsNullOrWhiteSpace(l.Sku) ? "" : l.Sku + "  ") + Pick(l.DescriptionEn, l.DescriptionBn),
                Int(l.Quantity, bn) + " " + l.Unit, Money(l.UnitPriceMinor ?? l.DisplayUnitPriceMinor ?? 0, bn) };
            if (discounts) cells.Add(Money(l.DiscountMinor, bn));
            cells.AddRange([Money(l.NetMinor, bn), Percent(l.VatRate, bn), Money(l.VatMinor, bn), Money(l.TotalMinor, bn)]);
            return (IReadOnlyList<string>)cells;
        }).ToList();
        var (weights, fontSize, landscape) = FitTable(header, rows);
        blocks.Add(new PdfTable(header, rows, MoneyColumn: header.Count - 1, ColumnWeights: weights, Branded: true, FontSize: fontSize));

        var totals = new List<KeyValuePair<string, string>> { new(L("Total excl. VAT", "মোট (ভ্যাট বাদে)"), Money(inv.NetMinor, bn)) };
        if (inv.SdMinor != 0) totals.Add(new(L("Supplementary duty", "সম্পূরক শুল্ক"), Money(inv.SdMinor, bn)));
        totals.Add(new(L("VAT", "ভ্যাট"), Money(inv.VatMinor, bn)));
        totals.Add(new(L("Total (BDT)", "সর্বমোট (টাকা)"), Money(inv.TotalMinor, bn)));
        blocks.Add(new PdfTotals(totals));
        blocks.Add(new PdfParagraph(L("Amount in words: ", "কথায়: ") + Words(inv.TotalMinor, bn)));

        string verify = inv.ContentHash.Length >= 16 ? inv.ContentHash[..16] : inv.ContentHash;
        var footerLines = new List<string>();
        if (!inv.ConsultantChecked)
            footerLines.Add(L("Prepared to the NBR Mushak-6.3 format; not yet checked by a VAT consultant", "এনবিআর মূসক-৬.৩ ফরম্যাট অনুযায়ী প্রস্তুত; ভ্যাট পরামর্শক এখনো যাচাই করেননি"));
        string title = $"{(credit ? L("Credit note", "ক্রেডিট নোট") : L("Tax invoice", "কর চালানপত্র"))} {inv.Number}";
        return new PdfDocumentModel(title, locale, blocks, $"{Digits(inv.Number, bn)} · {L("verification", "যাচাই")} {verify}", footerLines,
            specimen ? L("SPECIMEN · NOT A TAX INVOICE", "নমুনা · কর চালান নয়") : null, landscape);
    }

    private const float Margin = 48, CellPad = 4;
    private static readonly float[] Sizes = [10.5f, 10f, 9.5f, 9f, 8.5f, 8f, 7.5f, 7f];

    /// <summary>
    /// Column widths (points) so that no numeric cell and no single header word ever wraps; Description (column 1) takes what is left and may wrap.
    /// The table font steps down from the body size to 7 pt; if Description would still be squeezed, the whole invoice goes on landscape A4. Pure
    /// measurement of the stored text, so the same snapshot always gives the same layout.
    /// </summary>
    private static (double[] Weights, float FontSize, bool Landscape) FitTable(IReadOnlyList<string> header, IReadOnlyList<IReadOnlyList<string>> rows)
    {
        foreach (bool landscape in new[] { false, true })
        {
            float avail = (landscape ? 842 : 595) - 2 * Margin;
            foreach (float size in Sizes)
            {
                var w = new double[header.Count];
                for (int c = 0; c < w.Length; c++)
                {
                    if (c == 1) continue;
                    float widest = header[c].Split(' ').Max(word => PdfWidth(word, true, size));
                    foreach (var r in rows) widest = Math.Max(widest, PdfWidth(r[c], false, size));
                    w[c] = Math.Ceiling(widest) + 2 * CellPad + 2;
                }

                double left = avail - w.Sum();
                if (left >= 6 * size || (landscape && size == Sizes[^1])) { w[1] = Math.Max(left, 1); return (w, size, landscape); }
            }
        }

        throw new InvalidOperationException("unreachable");
    }

    private static float PdfWidth(string text, bool bold, float size) => SkiaPdfRenderer.MeasureWidth(text, bold, size);

    public static PdfResult Render(IPdfRenderer renderer, Invoice inv, string locale, string? originalNumber = null) =>
        renderer.Render(Compose(inv, locale, originalNumber));
}
