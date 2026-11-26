using Mechware.Application.Quotes;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Pdf;
using Mechware.Infrastructure.Quotes;
using UglyToad.PdfPig;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>D4.11 (ADR-0011 §10; threat model QT9, QT17): what the quote PDF is built from and how it reads, without a database.</summary>
public class QuotePdfTests
{
    private static readonly SkiaPdfRenderer Renderer = new();

    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Concat(doc.GetPages().SelectMany(p => p.GetWords().Select(w => w.Text)));
    }

    private static byte[] Render(QuotePdfModel model, QuotePdfOptions? options = null) =>
        Renderer.Render(QuotePdfComposer.Compose(model, options ?? new QuotePdfOptions(), null)).Bytes;

    [Theory]
    [InlineData(0, false, "Taka Zero Only")]
    [InlineData(12_345_00, false, "Taka Twelve Thousand Three Hundred Forty Five Only")]
    [InlineData(100_000_00, false, "Taka One Lakh Only")]
    [InlineData(2_50_00_000_75, false, "Taka Two Crore Fifty Lakh and Seventy Five Paisa Only")]
    [InlineData(12_345_00, true, "টাকা বারো হাজার তিনশ পঁয়তাল্লিশ মাত্র")]
    [InlineData(100_000_00, true, "টাকা এক লাখ মাত্র")]
    [InlineData(99_99, true, "টাকা নিরানব্বই এবং নিরানব্বই পয়সা মাত্র")]
    public void AmountInWords_ReadsInEnglishAndBangla(long minor, bool bn, string expected) =>
        Assert.Equal(expected, PdfText.Words(minor, bn));

    [Fact]
    public void MoneyAndDates_FollowTheLocale()
    {
        Assert.Equal("1,234,567.80", PdfText.Money(123_456_780, false));
        Assert.Equal("১২,৩৪,৫৬৭.৮০", PdfText.Money(123_456_780, true));
        Assert.Equal("2 Dec 2026", PdfText.Date(new DateOnly(2026, 12, 2), false));
        Assert.Equal("২ ডিসেম্বর ২০২৬", PdfText.Date(new DateOnly(2026, 12, 2), true));
        Assert.Equal("১৫%", PdfText.Percent(0.15m, true));
    }

    [Fact]
    public void TheModel_IsBuiltFromTheFrozenSnapshot_NeverFromTheLiveCompanyName()
    {
        var version = Sent().Version(1);
        var model = QuotePdfComposer.Model("MWQ-261117-ABCDEF", version, "en", draft: false, liveCompanyName: "Renamed Live Ltd");
        Assert.Equal("Acme Engineering Ltd", model.CompanyName);
        Assert.Equal("000123456-0101", model.CompanyBin);
        Assert.Equal(new DateOnly(2026, 11, 17), model.IssuedOn);
        Assert.Equal(new DateOnly(2026, 12, 2), model.ValidUntil);   // 15 days from the Dhaka send date, the date the version really expires on
        Assert.Equal(version.Lines.Count, model.Lines.Count);
        Assert.Equal(version.GrandTotalMinor, model.GrandTotalMinor);

        var draft = QuotePdfComposer.Model("MWQ-261117-ABCDEF", Drafted().Version(1), "en", draft: true, liveCompanyName: "Live Name Ltd");
        Assert.Equal("Live Name Ltd", draft.CompanyName);   // no snapshot yet: the preview falls back to the live name
        Assert.Null(draft.ValidUntil);
    }

    [Fact]
    public void ThePdf_PrintsTheRealValidUntilDate_AndLeavesOutEmptyFacts()
    {
        var model = QuotePdfComposer.Model("MWQ-261117-ABCDEF", Sent().Version(1), "en", false, null);
        var text = Text(Render(model));
        Assert.Contains("thisquotationisvaliduntil2Dec2026", text);
        Assert.DoesNotContain("workingdays", text.Replace("7workingdaysafter", ""));   // the pad's "7 working days" validity line is not used (delivery says 7 working days)
        Assert.DoesNotContain("TIN:", text);
        Assert.DoesNotContain("Tradelicence", text);
        Assert.DoesNotContain("BIN:000000", text);
        Assert.Contains("pricesincludeVATasshown", text);
        Assert.Contains("+8801748-305931", text);

        var withFacts = Text(Render(model, new QuotePdfOptions { Bin = "BIN-777", Tin = "TIN-888", TradeLicence = "TL-999" }));
        Assert.Contains("BIN:BIN-777", withFacts);
        Assert.Contains("TIN:TIN-888", withFacts);
        Assert.Contains("TL-999", withFacts);
    }

    [Fact]
    public void TheBanglaPdf_ShowsBanglaLabelsAndDigits_AndDiffersFromTheEnglishFile()
    {
        var version = Sent().Version(1);
        var bn = Render(QuotePdfComposer.Model("MWQ-261117-ABCDEF", version, "bn", false, null));
        var en = Render(QuotePdfComposer.Model("MWQ-261117-ABCDEF", version, "en", false, null));
        var text = Text(bn);
        // Skia writes the text map from the font's cmap, so joined forms and vowel signs do not read back as Unicode (ADR-0009): digits and Bengali letters are what can be checked.
        Assert.Contains("২০২৬", text);
        Assert.Contains(text, c => c is >= 'ঀ' and <= '৿');
        Assert.DoesNotContain("Quotation", text);
        Assert.NotEqual(Convert.ToHexString(en), Convert.ToHexString(bn));
        Assert.Equal(Convert.ToHexString(bn), Convert.ToHexString(Render(QuotePdfComposer.Model("MWQ-261117-ABCDEF", version, "bn", false, null))));   // deterministic
    }

    // QT17 (PDF half; the e-mail half is D4.12): text a person typed is printed as text, never as markup or PDF syntax.
    [Fact]
    public void QuoteTextOnThePdfAndInEmails_IsNeverInterpretedAsMarkup()
    {
        const string evil = "<b>Bold</b> & <script>alert(1)</script> ) Tj /F1 99 Tf (x \\) %%EOF";
        var quote = Drafted();
        quote.EditDraft(1, Content(15, Line(V1)) with { TermsText = evil, BuyerNote = "<i>note</i>" }, Staff(Author), Options, Now);
        quote.Send(1, Issue(quote) with { Company = new QuoteCompanySnapshot("<u>A&B</u> Ltd", "<BIN>", "1 <br/> Road"), PreparerName = "<img src=x>" }, Staff(Sender), false, Options, Now);

        var bytes = Render(QuotePdfComposer.Model("MWQ-261117-ABCDEF", quote.Version(1), "en", false, null));
        var text = Text(bytes);
        Assert.Contains("<b>Bold</b>&<script>alert(1)</script>)Tj/F199Tf(x\\)%%EOF", text);
        Assert.Contains("<u>A&B</u>Ltd", text);
        Assert.Contains("<img src=x>".Replace(" ", ""), text);
        Assert.Contains("<i>note</i>", text);
    }
}
