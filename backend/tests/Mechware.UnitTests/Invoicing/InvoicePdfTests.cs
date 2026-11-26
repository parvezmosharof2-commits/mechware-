using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using FluentAssertions;
using Mechware.Application.Pdf;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Invoicing.Pdf;
using Mechware.Infrastructure.Pdf;
using UglyToad.PdfPig;

namespace Mechware.UnitTests.Invoicing;

/// <summary>E6.4a (ADR-0012 section 12; threat model IT7, IT17): the invoice PDF is a pure function of the stored snapshot.</summary>
public class InvoicePdfTests
{
    private static readonly SkiaPdfRenderer Renderer = new();
    private static readonly string GoldenFile = Path.Combine(Path.GetDirectoryName(Here())!, "Golden", "invoice-pdf-sha256.txt");

    private static string Here([CallerFilePath] string p = "") => p;

    private static Invoice Company() => InvoiceFixtures.CompanyInvoice().Invoice;

    private static Invoice Personal(string buyer = "Nusrat Jahan", string sku = "SNS-1")
    {
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line(sku, 3, 10_000, 0.15m)], 5_000, null, out var shipment);
        return InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment, buyerName: buyer), InvoiceFixtures.Live);
    }

    /// <summary>A specimen with no issuer facts at all (the consultant sample before the owner has a BIN).</summary>
    private static Invoice Specimen()
    {
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("PLC-1200", 2, 4_550_000, 0.15m)], 12_000, null, out var shipment);
        return InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment, series: InvoiceSeries.SpecimenTaxInvoice),
            new InvoicingOptions { Mode = InvoicingMode.Specimen });
    }

    /// <summary>The domain has no credit-note constructor until E6.5a: the 6.7 layout is checked on a tax invoice re-shaped as a credit note.</summary>
    private static Invoice CreditNote()
    {
        var inv = Company();
        typeof(Invoice).GetProperty(nameof(Invoice.Kind))!.SetValue(inv, InvoiceKind.CreditNote);
        typeof(Invoice).GetProperty(nameof(Invoice.OriginalInvoiceId))!.SetValue(inv, Guid.NewGuid());
        return inv;
    }

    private static Invoice BigDiscounted()
    {
        var company = InvoiceFixtures.ActiveCompany();
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("BIG-1", 2, 12_345_678_900, 0.15m, 12_345_678_900), InvoiceFixtures.Line("SMALL-1", 1, 1_000, 0.15m)], 12_000,
            company.Id, out var shipment);
        return InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment, company, InvoiceFixtures.Site(company)), InvoiceFixtures.Live);
    }

    /// <summary>Test values only: never a real account or BIN.</summary>
    private static InvoicingOptions Print => new()
    {
        Bank = new InvoicingBankOptions { AccountName = "Test Trading Ltd", AccountNumber = "1234567890123", BankName = "Test Bank PLC", Branch = "Gulshan Branch", RoutingNumber = "123456789" },
        Signatory = new InvoicingSignatoryOptions { Name = "Test Signatory", Designation = "Authorised Signatory" }
    };

    /// <summary>The owner's invoice: PO, quotation no. and date, a discount, a dashed BIN in the config.</summary>
    private static Invoice Full()
    {
        var company = InvoiceFixtures.ActiveCompany();
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("PLC-1200", 2, 4_550_000, 0.15m, 100_000), InvoiceFixtures.Line("CBL-10", 25, 12_345, 0.075m)], 12_000,
            company.Id, out var shipment, po: "PO-2026/77", orderId: Guid.Parse("0d000000-0000-4000-8000-000000000008"));
        return InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment, company, InvoiceFixtures.Site(company), quoteDate: new DateOnly(2026, 11, 10)),
            InvoiceFixtures.Live with { Bin = "000123456-7890" });
    }

    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join(" ", doc.GetPages().SelectMany(p => p.GetWords().Select(w => w.Text)));
    }

    private static PdfResult Render(Invoice i, string locale, string? original = null, InvoicingOptions? print = null) => InvoicePdfComposer.Render(Renderer, i, locale, original, print);

    /// <summary>Fixtures draw random row ids, which feed the content hash printed in the footer; the golden files pin the layout, so the hash is fixed here.</summary>
    private static Invoice Pinned(Invoice i)
    {
        typeof(Invoice).GetProperty(nameof(Invoice.ContentHash))!.SetValue(i, new string('a', 64));
        return i;
    }

    private static IEnumerable<(string Name, Invoice Invoice, string Locale, string? Original, InvoicingOptions? Print)> Cases()
    {
        yield return ("company-en", Pinned(Company()), "en", null, null);
        yield return ("company-bn", Pinned(Company()), "bn", null, null);
        yield return ("personal-en", Pinned(Personal()), "en", null, null);
        yield return ("specimen-en", Pinned(Specimen()), "en", null, null);
        yield return ("specimen-bn", Pinned(Specimen()), "bn", null, null);
        yield return ("crore-discount-en", Pinned(BigDiscounted()), "en", null, null);
        yield return ("crore-discount-bn", Pinned(BigDiscounted()), "bn", null, null);
        yield return ("creditnote-en", Pinned(CreditNote()), "en", "MW-INV-2026-27-000482", null);
        yield return ("full-en", Pinned(Full()), "en", null, Print);
        yield return ("full-bn", Pinned(Full()), "bn", null, Print);
    }

    [Fact]
    public void TheInvoicePdf_IsByteIdenticalForTheSameSnapshot_AndItsHashIsRecorded()
    {
        var lines = new List<string>();
        foreach (var (name, invoice, locale, original, print) in Cases())
        {
            var first = Render(invoice, locale, original, print);
            var second = Render(invoice, locale, original, print);
            second.Bytes.Should().Equal(first.Bytes, name);
            first.Sha256Hex.Should().Be(Convert.ToHexStringLower(SHA256.HashData(first.Bytes)), name);
            lines.Add($"{name} {first.Sha256Hex}");
            if (Environment.GetEnvironmentVariable("MW_SAMPLE_DIR") is { Length: > 0 } dir) File.WriteAllBytes(Path.Combine(dir, $"invoice-{name}.pdf"), first.Bytes);
        }

        if (Environment.GetEnvironmentVariable("MW_UPDATE_GOLDEN") == "1") File.WriteAllLines(GoldenFile, lines);
        File.ReadAllLines(GoldenFile).Should().Equal(lines, "golden hashes of the ten reference PDFs (MW_UPDATE_GOLDEN=1 regenerates after a deliberate layout change)");
    }

    [Fact]
    public void ThePdfIsBuiltOnlyFromTheSnapshot_LaterCompanyOrConfigChangesDoNotShow()
    {
        var (invoice, _, company, _) = InvoiceFixtures.CompanyInvoice();
        var before = Render(invoice, "en");
        company.GetType().GetProperty("LegalName")!.SetValue(company, "Renamed Later Ltd");
        company.GetType().GetProperty("Bin")!.SetValue(company, "9999999999999");
        _ = InvoiceFixtures.Live with { Bin = "1111111111111", LegalNameEn = "Other Seller", ConsultantChecked = true };

        var after = Render(invoice, "en");
        after.Bytes.Should().Equal(before.Bytes);
        var text = Text(after.Bytes);
        text.Should().Contain("Square").And.Contain("001234567-8901").And.Contain("000123456-7890").And.NotContain("Renamed").And.NotContain("9999999999999");
        text.Should().Contain("not yet checked by a VAT consultant");
    }

    [Fact]
    public void TheMushak63Fields_ArePrinted_InEnglish()
    {
        var invoice = Company();
        var text = Text(Render(invoice, "en").Bytes);
        text.Should().Contain("25 pcs").And.Contain("2 pcs", "the unit of supply prints on every line, next to the quantity");
        foreach (var fact in new[] { "Mushak-6.3", invoice.Number, invoice.PrintedIssueTime[..10], "Square", "001234567-8901", "000123456-7890", "123456789012",
                     "PO-2026/77", "Tongi", "Steadfast", "SF-123", "PLC-1200", "Delivery", "Amount in words:", "Taka", "Only", invoice.ContentHash[..16] })
            text.Should().Contain(fact);
        text.Should().Contain(PdfText.Money(invoice.TotalMinor, false));
    }

    private static long Poisha(string printed) =>
        long.Parse(new string(printed.Select(c => c is >= '০' and <= '৯' ? (char)('0' + c - '০') : c).Where(char.IsDigit).ToArray()));

    [Theory]
    [InlineData("en")]
    [InlineData("bn")]
    public void EveryLine_QuantityTimesUnitPriceMinusDiscount_EqualsNet_AsStoredAndAsPrinted(string locale)
    {
        var invoice = Company();
        invoice.Lines.Any(l => l.DiscountMinor > 0).Should().BeTrue("the fixture carries a discount");
        foreach (var l in invoice.Lines) (l.Quantity * l.UnitPriceMinor!.Value - l.DiscountMinor).Should().Be(l.NetMinor, $"line {l.LineNo} as stored");

        var table = InvoicePdfComposer.Compose(invoice, locale).Blocks.OfType<PdfTable>().Single();
        table.Header.Should().Contain(locale == "bn" ? "ছাড়" : "Discount");
        int qty = 2, price = 3, disc = 4, net = 5;
        for (int i = 0; i < table.Rows.Count; i++)
        {
            var r = table.Rows[i];
            (Poisha(r[qty].Split(' ')[0]) * Poisha(r[price]) - Poisha(r[disc])).Should().Be(Poisha(r[net]), $"row {i + 1} as printed");
        }

        var text = Text(Render(invoice, locale).Bytes);
        text.Should().Contain(PdfText.Money(100_000, locale == "bn"), "the discount is on the page");
        InvoicePdfComposer.Compose(Personal(), locale).Blocks.OfType<PdfTable>().Single().Header.Should().NotContain(locale == "bn" ? "ছাড়" : "Discount", "no discount, no column");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("bn")]
    public void NumericCells_NeverWrap_EvenForACroreAmountOnADiscountedLine(string locale)
    {
        bool bn = locale == "bn";
        var invoice = BigDiscounted();   // 2 x 12,34,56,789.00 less a discount of 12,34,56,789.00: unit price, discount and net all print as 12,34,56,789.00
        var model = InvoicePdfComposer.Compose(invoice, locale);
        var bytes = Render(invoice, locale).Bytes;
        Render(invoice, locale).Bytes.Should().Equal(bytes, "layout is a pure function of the snapshot");
        (model.Landscape || model.Blocks.OfType<PdfTable>().Single().FontSize >= 7f).Should().BeTrue();

        var words = Text(bytes).Split(' ');
        var widest = PdfText.Money(12_345_678_900, bn);
        widest.Should().Be(bn ? "১২,৩৪,৫৬,৭৮৯.০০" : "123,456,789.00");
        words.Count(w => w == widest).Should().BeGreaterThanOrEqualTo(3, "unit price, discount and net each print whole");
        foreach (var l in invoice.Lines) foreach (var v in new[] { l.UnitPriceMinor ?? 0, l.DiscountMinor, l.NetMinor, l.VatMinor, l.TotalMinor }) words.Should().Contain(PdfText.Money(v, bn));
        words.Should().Contain(PdfText.Percent(0.15m, bn));
    }

    [Fact]
    public void TheBanglaPdf_UsesBanglaDigitsWordsAndLabels()
    {
        var invoice = Company();
        var text = Text(Render(invoice, "bn").Bytes);
        text.Should().Contain("১,০৬,৯৩৭.৭২").And.Contain("২১-১১-২০২৬").And.Contain("২৫ pcs").And.Contain("টাকা");   // conjunct words come out of the text extractor in pieces, so only whole-glyph text is asserted
        text.Should().NotContain(PdfText.Money(invoice.TotalMinor, false), "money has no ASCII digits in Bangla");
        text.Should().Contain(PdfText.Digits(invoice.PrintedIssueTime[..10], true));
    }

    [Fact]
    public void ASpecimen_IsWatermarked_AndAMissingBinNeverPrintsAsANumber()
    {
        var model = InvoicePdfComposer.Compose(Specimen(), "en");
        model.Watermark.Should().Be("SPECIMEN · NOT A TAX INVOICE");
        InvoicePdfComposer.Compose(Company(), "en").Watermark.Should().BeNull();
        var text = Text(Renderer.Render(model).Bytes);
        text.Should().Contain("BIN: [missing]").And.Contain("TIN: [missing]");
        Regex.IsMatch(text, @"BIN: \d").Should().BeFalse();
    }

    [Fact]
    public void EveryInvoicePdf_CarriesTheConsultantCheckFooter_UntilTheFlagIsSet()
    {
        foreach (var (name, invoice, locale, original, _) in Cases())
        {
            invoice.ConsultantChecked.Should().BeFalse(name);
            InvoicePdfComposer.Compose(invoice, locale, original).FooterLines.Should().ContainSingle(l => l.Contains(locale == "bn" ? "পরামর্শক" : "VAT consultant"), name);
        }

        var checkedInvoice = Company();
        typeof(Invoice).GetProperty(nameof(Invoice.ConsultantChecked))!.SetValue(checkedInvoice, true);
        InvoicePdfComposer.Compose(checkedInvoice, "en").FooterLines.Should().BeEmpty();
    }

    [Theory]
    [InlineData("en")]
    [InlineData("bn")]
    public void TheOwnersInvoiceBlocks_ArePrinted_AndTheTaxFieldsStay(string locale)
    {
        var invoice = Full();
        invoice.QuoteDate.Should().Be(new DateOnly(2026, 11, 10));
        var model = InvoicePdfComposer.Compose(invoice, locale, null, Print);
        var paragraphs = model.Blocks.OfType<PdfParagraph>().Select(p => p.Text).ToList();
        var keyValues = model.Blocks.OfType<PdfKeyValues>().SelectMany(k => k.Rows).ToList();
        bool bn = locale == "bn";
        keyValues.Should().Contain(r => r.Key == (bn ? "রেফারেন্স পিও" : "Reference PO") && r.Value == "PO-2026/77");
        keyValues.Should().Contain(r => r.Key == (bn ? "কোটেশন নং" : "Against quotation no.") && r.Value.StartsWith("MWQ-2026-000012") && r.Value.Contains(PdfText.Date(new DateOnly(2026, 11, 10), bn)));
        paragraphs.Should().Contain(p => p.StartsWith(bn ? "বিষয়: অর্ডার নং " : "Subject: Supply of goods against order ") && p.Contains(invoice.OrderNumber));
        var bankBlock = paragraphs.Should().ContainSingle(p => p.StartsWith(bn ? "পরিশোধের শর্ত ও ব্যাংকের তথ্য" : "Payment terms & bank details")).Subject;
        bankBlock.Should().Contain("Test Trading Ltd").And.Contain("1234567890123").And.Contain("Test Bank PLC").And.Contain("Gulshan Branch").And.Contain("123456789");
        paragraphs.Should().Contain(p => p.Contains("Test Signatory") && p.Contains("Authorised Signatory"));
        var text = Text(Renderer.Render(model).Bytes);
        text.Should().Contain("000123456-7890").And.Contain("001234567-8901").And.Contain("123456789012").And.Contain(PdfText.Digits(invoice.Number, bn));
    }

    [Fact]
    public void WithoutBankConfig_NoBankBlockPrints_AndTheSignatoryFallsBackToTheSnapshot()
    {
        var model = InvoicePdfComposer.Compose(Full(), "en", null, new InvoicingOptions());
        model.Blocks.OfType<PdfParagraph>().Should().NotContain(p => p.Text.Contains("bank details"));
        model.Blocks.OfType<PdfParagraph>().Should().Contain(p => p.Text.Contains("Md. Parvez Mosharaf") && p.Text.Contains("Managing Director"));
        InvoicePdfComposer.Compose(Full(), "en").Blocks.OfType<PdfParagraph>().Should().NotContain(p => p.Text.Contains("bank details"));
        var partial = InvoicePdfComposer.Compose(Full(), "en", null, new InvoicingOptions { Bank = new InvoicingBankOptions { AccountNumber = "99" } });
        partial.Blocks.OfType<PdfParagraph>().Single(p => p.Text.Contains("bank details")).Text.Should().Contain("Account number: 99").And.NotContain("Branch");
    }

    [Fact]
    public void AnOrderWithoutPoOrQuote_PrintsNeitherLine_AndTheSubjectStays()
    {
        var model = InvoicePdfComposer.Compose(Personal(), "en");
        var keys = model.Blocks.OfType<PdfKeyValues>().SelectMany(k => k.Rows).Select(r => r.Key).ToList();
        keys.Should().NotContain("Reference PO").And.NotContain("Against quotation no.");
        model.Blocks.OfType<PdfParagraph>().Should().Contain(p => p.Text.StartsWith("Subject: Supply of goods against order "));
    }

    [Fact]
    public void TheQuoteDate_IsInTheSnapshotAndInTheHash_AndV1InvoicesStillVerify()
    {
        var v1 = Company();
        v1.QuoteDate.Should().BeNull();
        System.Text.Encoding.UTF8.GetString(InvoiceContentHash.CanonicalJson(v1)).Should().Contain("\"schema\":\"mechware.invoice.v1\"").And.NotContain("quoteDate");
        InvoiceContentHash.Matches(v1, v1.ContentHash).Should().BeTrue();
        var v2 = Full();
        System.Text.Encoding.UTF8.GetString(InvoiceContentHash.CanonicalJson(v2)).Should().Contain("\"schema\":\"mechware.invoice.v2\"").And.Contain("\"quoteDate\":\"2026-11-10\"");
        InvoiceContentHash.Matches(v2, v2.ContentHash).Should().BeTrue();
        typeof(Invoice).GetProperty(nameof(Invoice.QuoteDate))!.SetValue(v2, new DateOnly(2026, 11, 11));
        InvoiceContentHash.Matches(v2, v2.ContentHash).Should().BeFalse("the date is hashed");
    }

    [Fact]
    public void TheCreditNote_UsesThe67Heading_AndNamesTheOriginalInvoice()
    {
        var text = Text(Render(CreditNote(), "en", "MW-INV-2026-27-000482").Bytes);
        text.Should().Contain("Mushak-6.7").And.Contain("MW-INV-2026-27-000482").And.NotContain("Mushak-6.3)");
    }

    [Fact]
    public void TextFromTheSnapshot_IsNeverInterpretedAsMarkup()   // the PDF part of IT17
    {
        const string evil = "<b>Bold</b> {0} %s &amp; <script>alert(1)</script>";
        var bytes = Render(Personal(buyer: evil, sku: "<i>SKU</i>"), "en").Bytes;
        var text = Text(bytes);
        text.Should().Contain("<b>Bold</b>").And.Contain("{0}").And.Contain("%s").And.Contain("&amp;").And.Contain("<script>alert(1)</script>").And.Contain("<script>");
        text.Replace(" ", "").Should().Contain("<i>SKU</i>");   // the narrow description column may wrap the SKU
        System.Text.Encoding.Latin1.GetString(bytes).Should().NotContain("/JavaScript").And.NotContain("/URI").And.NotContain("/Launch");
    }
}
