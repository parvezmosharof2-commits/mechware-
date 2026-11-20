using Mechware.Application.Pdf;
using Mechware.Application.Quotes;
using Mechware.Domain.Quotes;
using static Mechware.Infrastructure.Quotes.QuotePdfText;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// Builds the <see cref="QuotePdfModel"/> and lays it out (ADR-0011 §10, QT9, QT17). The model reads the version row and its lines only: the buyer
/// company is the snapshot of the send; <paramref name="liveCompanyName"/> is used only when there is no snapshot, that is for a draft preview.
/// Every text is handed to the renderer as plain text, so nothing a person typed is ever read as markup.
/// </summary>
internal static class QuotePdfComposer
{
    public static QuotePdfModel Model(string number, QuoteVersion v, string locale, bool draft, string? liveCompanyName, QuoteTermsSnapshot? liveTerms = null)
    {
        bool bn = locale == "bn";
        var issued = v.SentAtUtc is { } sent ? QuoteValidity.DhakaDate(sent) : (DateOnly?)null;
        var until = v.SentAtUtc is { } s ? QuoteValidity.LastValidDhakaDate(s, v.ValidityDays) : (DateOnly?)null;
        return new QuotePdfModel(locale, draft, number, v.VersionNo, issued, until, v.ValidityDays,
            v.CompanySnapshot?.Name ?? liveCompanyName ?? "", v.CompanySnapshot?.Bin, v.CompanySnapshot?.BillingAddress, v.ShipTo,
            StaffQuoteViews.Wire(v.PaymentTerms), StaffQuoteViews.Wire(v.ShippingMode),
            [.. v.Lines.OrderBy(l => l.LineNo).Select(l => new QuotePdfLine(l.LineNo, l.PartNumber, l.Sku, bn && l.TitleBn.Length > 0 ? l.TitleBn : l.TitleEn,
                l.Quantity, l.UnitPriceMinor, l.VatRate, l.LineTotalMinor))],
            v.GoodsNetMinor, v.TaxMinor, v.GoodsTotalMinor, v.ShippingTotalMinor, v.GrandTotalMinor, v.TermsText, v.BuyerNote, v.PreparerName, v.ContentHash,
            v.TermsSnapshot ?? liveTerms);
    }

    public static PdfDocumentModel Compose(QuotePdfModel m, QuotePdfOptions o, byte[]? logo)
    {
        bool bn = m.Locale == "bn";
        string L(string en, string bangla) => bn ? bangla : en;
        string D(DateOnly d) => Date(d, bn);
        string reference = $"{m.Number} rev. {Int(m.VersionNo, bn)}";
        var blocks = new List<PdfBlock>();

        var registration = new List<string>();
        void Fact(string label, string value) { if (!string.IsNullOrWhiteSpace(value)) registration.Add($"{label}: {value.Trim()}"); }
        Fact("BIN", o.Bin);
        Fact("TIN", o.Tin);
        Fact(L("Trade licence", "ট্রেড লাইসেন্স"), o.TradeLicence);
        blocks.Add(new PdfBand(o.CompanyName, string.IsNullOrWhiteSpace(o.Tagline) ? null : o.Tagline, registration, logo));
        if (m.Draft) blocks.Add(new PdfBand(L("DRAFT, NOT AN OFFER", "খসড়া, এটি কোনো প্রস্তাব নয়"), null, [], Teal: true));

        blocks.Add(new PdfHeading(L("Quotation", "কোটেশন")));
        var box = new List<KeyValuePair<string, string>> { new(L("Quotation no.", "কোটেশন নং"), reference) };
        if (m.IssuedOn is { } issued) box.Add(new(L("Date", "তারিখ"), D(issued)));
        if (m.ValidUntil is { } until) box.Add(new(L("Valid until", "বৈধ থাকবে"), D(until)));
        else box.Add(new(L("Validity", "মেয়াদ"), L($"{m.ValidityDays} days from the date of issue", $"{Int(m.ValidityDays, true)} দিন, ইস্যুর তারিখ থেকে")));
        if (!string.IsNullOrWhiteSpace(m.PreparerName)) box.Add(new(L("Prepared by", "প্রস্তুতকারী"), m.PreparerName!));
        blocks.Add(new PdfKeyValues(box));

        var to = new List<string> { m.CompanyName };
        if (!string.IsNullOrWhiteSpace(m.CompanyBin)) to.Add("BIN: " + m.CompanyBin);
        if (!string.IsNullOrWhiteSpace(m.CompanyAddress)) to.Add(m.CompanyAddress!);
        blocks.Add(new PdfParagraph(L("Quoted to: ", "কোটেশন প্রাপক: ") + string.Join("\n", to)));
        if (m.ShipTo is { } s)
            blocks.Add(new PdfParagraph(L("Deliver to: ", "ডেলিভারি ঠিকানা: ") + string.Join(", ", new[] { s.SiteName, s.RecipientName, s.Phone, s.Line1, s.Line2, s.Upazila, s.District, s.Division, s.PostalCode }
                .Where(x => !string.IsNullOrWhiteSpace(x)))));

        blocks.Add(new PdfTable(
            [L("No.", "নং"), L("Part no.", "পার্ট নং"), L("Description", "বিবরণ"), L("Qty", "পরিমাণ"), L("Unit price (excl. VAT)", "একক মূল্য (ভ্যাট বাদে)"), L("VAT", "ভ্যাট"), L("Line total (incl. VAT)", "মোট (ভ্যাটসহ)")],
            [.. m.Lines.Select(l => (IReadOnlyList<string>)[Int(l.LineNo, bn), string.IsNullOrWhiteSpace(l.PartNumber) ? l.Sku : l.PartNumber!, l.Title, Int(l.Quantity, bn),
                Money(l.UnitPriceMinor, bn), Percent(l.VatRate, bn), Money(l.LineTotalMinor, bn)])],
            MoneyColumn: 6, ColumnWeights: [0.6, 1.6, 3, 0.9, 1.6, 0.9, 1.7], Branded: true));

        var totals = new List<KeyValuePair<string, string>>
        {
            new(L("Goods (excl. VAT)", "পণ্য (ভ্যাট বাদে)"), Money(m.GoodsNetMinor, bn)),
            new(L("VAT", "ভ্যাট"), Money(m.TaxMinor, bn)),
            new(L("Goods total (incl. VAT)", "পণ্য মোট (ভ্যাটসহ)"), Money(m.GoodsTotalMinor, bn)),
        };
        long payable = m.GoodsTotalMinor;
        if (m.ShippingTotalMinor is { } ship)
        {
            totals.Add(new(L("Delivery", "ডেলিভারি"), Money(ship, bn)));
            payable = m.GrandTotalMinor ?? m.GoodsTotalMinor + ship;
            totals.Add(new(L("Grand total (BDT)", "সর্বমোট (টাকা)"), Money(payable, bn)));
        }
        else
        {
            totals.Add(new(L("Delivery", "ডেলিভারি"), L("calculated at order", "অর্ডারের সময় নির্ধারিত")));
            totals.Add(new(L("Total (BDT)", "মোট (টাকা)"), Money(payable, bn)));
        }
        blocks.Add(new PdfTotals(totals));
        blocks.Add(new PdfParagraph(L("Amount in words: ", "কথায়: ") + Words(payable, bn)));

        blocks.Add(new PdfHeading(L("Terms and conditions", "শর্তাবলি")));
        string payment = m.PaymentTerms == "advance"
            ? L("Payment is in advance, before the order is released.", "পেমেন্ট অগ্রিম, অর্ডার ছাড়ার আগে।")
            : L("Payment is on the company's credit account terms.", "পেমেন্ট কোম্পানির ক্রেডিট অ্যাকাউন্টের শর্ত অনুযায়ী।");
        string stock = L("Stock is not reserved by this quotation and is confirmed when you accept it.", "এই কোটেশনে স্টক সংরক্ষিত নয়; গ্রহণের সময় নিশ্চিত করা হবে।");
        string untilText = m.ValidUntil is { } u ? D(u) : L($"{m.ValidityDays} days from the date of issue", $"ইস্যুর পর {Int(m.ValidityDays, true)} দিন");
        var terms = new[] { payment, stock }.Concat((bn ? m.Terms?.Bn ?? o.TermsBn : m.Terms?.En ?? o.TermsEn).Select(t => t.Replace("{validUntil}", untilText, StringComparison.Ordinal)))
            .Where(t => !string.IsNullOrWhiteSpace(t)).Select(t => "- " + t.Trim());
        blocks.Add(new PdfParagraph(string.Join("\n", terms)));
        if (!string.IsNullOrWhiteSpace(m.TermsText)) blocks.Add(new PdfParagraph(L("Terms for this quotation: ", "এই কোটেশনের শর্ত: ") + m.TermsText!.Trim()));
        if (!string.IsNullOrWhiteSpace(m.BuyerNote)) blocks.Add(new PdfParagraph(L("Note: ", "মন্তব্য: ") + m.BuyerNote!.Trim()));

        if (!string.IsNullOrWhiteSpace(o.SignatoryName))
            blocks.Add(new PdfParagraph(L("For ", "") + o.CompanyName + (bn ? " পক্ষে" : "") + "\n\n" + o.SignatoryName + (string.IsNullOrWhiteSpace(o.SignatoryTitle) ? "" : ", " + o.SignatoryTitle)));

        var contact = new[] { o.Office, o.Address, string.IsNullOrWhiteSpace(o.Phone) ? "" : L("Phone: ", "ফোন: ") + o.Phone, string.IsNullOrWhiteSpace(o.Email) ? "" : "Email: " + o.Email }
            .Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
        var footerLines = new List<string>();
        if (contact.Count > 0) footerLines.Add(string.Join(" · ", contact));
        if (!string.IsNullOrWhiteSpace(o.FooterLine)) footerLines.Add(o.FooterLine);

        string verify = m.ContentHash is { Length: >= 16 } h ? h[..16] : "";
        string footer = m.Draft ? $"{reference} · {L("DRAFT", "খসড়া")}" : $"{reference} · {L("verification", "যাচাই")} {verify}";
        return new PdfDocumentModel($"{L("Quotation", "কোটেশন")} {reference}", m.Locale, blocks, footer, footerLines);
    }
}
