using Mechware.Application.Pdf;
using Mechware.Infrastructure.Pdf;
using SkiaSharp;
using SkiaSharp.HarfBuzz;
using UglyToad.PdfPig;
using UglyToad.PdfPig.Content;

namespace Mechware.UnitTests.Pdf;

public class PdfRendererTests
{
    private static readonly SkiaPdfRenderer Renderer = new();

    private static PdfDocumentModel Doc(string locale, string title, IEnumerable<PdfBlock> blocks) => new(title, locale, [.. blocks]);

    private static PdfTable BigTable(int rows) => new(
        ["Item", "Qty", "Amount (BDT)"],
        [.. Enumerable.Range(1, rows).Select(i => (IReadOnlyList<string>)[$"Part {i:000} industrial bearing", i.ToString(), $"{i * 1250:N0}.00"])],
        MoneyColumn: 2, ColumnWeights: [3, 1, 2]);

    [Fact]
    public void Same_input_gives_identical_bytes_and_hash_and_different_input_differs()
    {
        PdfDocumentModel a = Doc("en", "Quote Q-1", [new PdfHeading("Quote"), new PdfParagraph("Hello"), BigTable(5)]);
        PdfResult r1 = Renderer.Render(a), r2 = new SkiaPdfRenderer().Render(a with { });
        Assert.Equal(r1.Bytes, r2.Bytes);
        Assert.Equal(r1.Sha256Hex, r2.Sha256Hex);
        Assert.Matches("^[0-9a-f]{64}$", r1.Sha256Hex);
        Assert.NotEqual(r1.Sha256Hex, Renderer.Render(Doc("en", "Quote Q-1", [new PdfHeading("Quote"), new PdfParagraph("Hello!"), BigTable(5)])).Sha256Hex);
    }

    [Fact]
    public void Metadata_is_fixed_so_no_clock_or_random_id_leaks_into_the_file()
    {
        string text = System.Text.Encoding.Latin1.GetString(Renderer.Render(Doc("en", "T", [new PdfParagraph("x")])).Bytes);
        Assert.Contains("D:20000101000000", text);
        Assert.Contains("Mechware", text);
        Assert.DoesNotContain(DateTime.UtcNow.Year.ToString() + "0", text.Replace("D:20000101", ""), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("en")]
    [InlineData("bn")]
    public void Long_table_spans_pages_with_footer_and_repeated_header(string locale)
    {
        string head = locale == "bn" ? "পণ্য" : "Item";
        var table = BigTable(120) with { Header = [head, "Qty", "Amount"] };
        PdfResult r = Renderer.Render(Doc(locale, locale == "bn" ? "কোটেশন" : "Quote", [new PdfHeading(locale == "bn" ? "মূল্যের প্রস্তাব" : "Price quote"), table]));

        Assert.StartsWith("%PDF-", System.Text.Encoding.ASCII.GetString(r.Bytes, 0, 5));
        using PdfDocument pdf = PdfDocument.Open(r.Bytes);
        Assert.True(r.PageCount >= 3);
        Assert.Equal(r.PageCount, pdf.NumberOfPages);
        string last = string.Concat(pdf.GetPage(r.PageCount).Letters.Select(l => l.Value));
        Assert.Contains(r.PageCount.ToString(), last);   // "page x of y" carries the true total on the last page
        if (locale == "en") Assert.Contains($"Page{r.PageCount}of{r.PageCount}", last.Replace(" ", ""));
        Assert.Contains("Qty", string.Concat(pdf.GetPage(2).Letters.Select(l => l.Value)));   // header repeats
    }

    [Fact]
    public void Money_column_is_right_aligned()
    {
        using PdfDocument pdf = PdfDocument.Open(Renderer.Render(Doc("en", "T", [BigTable(3)])).Bytes);
        var words = pdf.GetPage(1).GetWords().ToList();
        double right1 = words.First(w => w.Text == "1,250.00").BoundingBox.Right, right3 = words.First(w => w.Text == "3,750.00").BoundingBox.Right;
        Assert.InRange(Math.Abs(right1 - right3), 0, 0.5);
        Assert.True(right1 > 500);
    }

    [Fact]
    public void Token_wider_than_its_column_is_broken_and_stays_inside_the_column()
    {
        string pn = "SKF-6205-2RSH-C3-GJN-" + new string('X', 60);
        var table = new PdfTable(["Part", "Qty", "Amount"], [[pn, "1", "1,250.00"]], MoneyColumn: 2, ColumnWeights: [1, 1, 1]);
        using PdfDocument pdf = PdfDocument.Open(Renderer.Render(Doc("en", "T", [table])).Bytes);
        var letters = pdf.GetPage(1).Letters.ToList();
        double colRight = 48 + (595 - 2 * 48) / 3.0;
        var part = letters.Where(l => l.Value == "X").ToList();
        Assert.Equal(60, part.Count);   // nothing lost
        Assert.All(part, l => Assert.True(l.GlyphRectangle.Right <= colRight, $"glyph at {l.GlyphRectangle.Right} crosses {colRight}"));
        Assert.True(part.Select(l => Math.Round(l.StartBaseLine.Y)).Distinct().Count() > 1);   // wrapped onto several lines
    }

    private static int GlyphsIn(string text)
    {
        int Count(string t)
        {
            using PdfDocument pdf = PdfDocument.Open(Renderer.Render(Doc("en", "T", [new PdfParagraph(t)])).Bytes);
            return pdf.GetPage(1).Letters.Count;
        }
        return Count(text) - Count("");   // subtract the footer's glyphs
    }

    private static (int Shaped, int Naive) Counts(string text)
    {
        using var shaper = new SKShaper(Face());
        using var font = new SKFont(Face(), 10.5f);
        return (shaper.Shape(text, font).Codepoints.Length, font.GetGlyphs(text).Length);
    }

    private static SKTypeface Face()
    {
        using Stream s = typeof(SkiaPdfRenderer).Assembly.GetManifestResourceStream("Mechware.Pdf.Fonts.HindSiliguri-Regular.ttf")!;
        return SKTypeface.FromStream(s);
    }

    // Conjuncts that the font joins into one glyph: naive per-codepoint rendering would emit one glyph per codepoint
    // (including the visible hasanta), shaping emits fewer. The PDF must carry the shaped run.
    [Theory]
    [InlineData("ক্ষ")]      // ksha
    [InlineData("ন্ত")]      // nta
    [InlineData("স্ত্র")]    // stra
    [InlineData("ঙ্গ")]      // nga
    [InlineData("র‍্যা")]     // ya-phala with ZWJ
    [InlineData("বাংলাদেশ")] // plain word with anusvara
    public void Bangla_conjuncts_are_shaped_into_the_pdf_glyph_run(string text)
    {
        (int shaped, int naive) = Counts(text);
        int inPdf = GlyphsIn(text);
        Assert.Equal(shaped, inPdf);
        if (text != "বাংলাদেশ") Assert.True(inPdf < naive, $"'{text}' should shape to fewer glyphs ({inPdf}) than codepoints ({naive}); shaping looks off.");
    }

    [Fact]
    public void Reordering_vowel_sign_is_drawn_before_its_consonant()
    {
        // ি (i-kar) is stored after the consonant but is drawn on its left.
        using PdfDocument pdf = PdfDocument.Open(Renderer.Render(Doc("bn", "T", [new PdfParagraph("কি")])).Bytes);
        var body = pdf.GetPage(1).Letters.Where(l => l.StartBaseLine.Y > 700).OrderBy(l => l.StartBaseLine.X).ToList();
        Assert.Equal(2, body.Count);
        Assert.Equal("ি", body[0].Value);
        Assert.Equal("ক", body[1].Value);
    }

    [Fact]
    public void Bangla_text_extracts_back_in_visual_order()
    {
        using PdfDocument pdf = PdfDocument.Open(Renderer.Render(Doc("bn", "T", [new PdfParagraph("বাংলাদেশ")])).Bytes);
        // Visual order after shaping: the e-kar of দেশ is drawn before দ, and আ-kar stays after its consonant.
        Assert.Contains("বাংলােদশ", string.Concat(pdf.GetPage(1).Letters.Select(l => l.Value)));
    }

    [Fact]
    public void Font_is_embedded_with_no_external_reference()
    {
        PdfResult r = Renderer.Render(Doc("bn", "T", [new PdfParagraph("বাংলা Bangla")]));
        string raw = System.Text.Encoding.Latin1.GetString(r.Bytes);
        Assert.Contains("FontFile2", raw);       // TrueType program inside the file
        Assert.Contains("HindSiliguri", raw);
        // Skia's build here embeds the whole face (compressed, ~120 KB) rather than a subset; see ADR-0009.
        Assert.True(r.Bytes.Length < 400_000, $"file is {r.Bytes.Length} bytes");
    }

    [Fact]
    public void Unknown_locale_is_rejected() =>
        Assert.Throws<ArgumentException>(() => Renderer.Render(Doc("fr", "T", [])));
}
