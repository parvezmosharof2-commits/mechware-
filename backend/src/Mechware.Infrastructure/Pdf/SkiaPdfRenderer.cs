using System.Security.Cryptography;
using System.Text;
using Mechware.Application.Pdf;
using SkiaSharp;
using SkiaSharp.HarfBuzz;

namespace Mechware.Infrastructure.Pdf;

/// <summary>
/// A4 PDF writer on Skia's PDF backend (no browser). Text is shaped with HarfBuzz so Bangla conjuncts, reordering vowel
/// signs and reph/ya-phala come out right; Hind Siliguri (SIL OFL) is embedded in the assembly and embedded whole in each file (this Skia build does not subset; ~120 KB per face, ADR-0009).
/// Determinism: fixed creation/modification dates and producer, no random ids (see ADR-0009).
/// </summary>
public sealed class SkiaPdfRenderer : IPdfRenderer
{
    private const float PageW = 595, PageH = 842, Margin = 48, Body = 10.5f, Lead = 15, Pad = 4;
    private static readonly DateTime FixedDate = new(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Lazy<SKTypeface> Regular = new(() => Load("HindSiliguri-Regular.ttf"));
    private static readonly Lazy<SKTypeface> Bold = new(() => Load("HindSiliguri-Bold.ttf"));

    public PdfResult Render(PdfDocumentModel model)
    {
        ArgumentNullException.ThrowIfNull(model);
        if (model.Locale is not ("en" or "bn")) throw new ArgumentException("Locale must be en or bn.", nameof(model));
        int pages = new Pass(model, 0).Run(out _);   // first pass counts pages; the second prints "page x of y"
        new Pass(model, pages).Run(out byte[] bytes);
        return new PdfResult(bytes, Convert.ToHexStringLower(SHA256.HashData(bytes)), pages);
    }

    private static SKTypeface Load(string file)
    {
        using Stream s = typeof(SkiaPdfRenderer).Assembly.GetManifestResourceStream("Mechware.Pdf.Fonts." + file)
            ?? throw new InvalidOperationException($"Embedded font {file} is missing.");
        return SKTypeface.FromStream(s) ?? throw new InvalidOperationException($"Font {file} could not be loaded.");
    }

    /// <summary>One layout-and-draw run over the model (not thread-safe; a new one per pass).</summary>
    private sealed class Pass(PdfDocumentModel model, int totalPages)
    {
        private readonly SKShaper _shapeR = new(Regular.Value), _shapeB = new(Bold.Value);
        private readonly SKFont _fontR = Font(Regular.Value, Body), _fontB = Font(Bold.Value, Body), _fontH = Font(Bold.Value, 16);
        private readonly SKPaint _ink = new() { Color = new SKColor(0x1F, 0x23, 0x28), IsAntialias = true };
        private readonly SKPaint _rule = new() { Color = new SKColor(0xC4, 0xC9, 0xCF), StrokeWidth = 0.5f };
        private SKDocument _doc = null!;
        private SKCanvas? _canvas;
        private int _page;
        private float _y;

        private static SKFont Font(SKTypeface face, float size) => new(face, size) { Subpixel = true, LinearMetrics = true };

        public int Run(out byte[] bytes)
        {
            using var ms = new MemoryStream();
            var meta = new SKDocumentPdfMetadata
            {
                Title = model.Title,
                Author = "Mechware",
                Creator = "Mechware",
                Producer = "Mechware",
                Creation = FixedDate,
                Modified = FixedDate,
                RasterDpi = 72,
            };
            using (_doc = SKDocument.CreatePdf(ms, meta))
            {
                NewPage();
                foreach (PdfBlock block in model.Blocks)
                {
                    switch (block)
                    {
                        case PdfHeading h:
                            Ensure(36);
                            _y += 14;
                            foreach (string l in Wrap(h.Text, PageW - 2 * Margin, _fontH, _shapeB)) { Text(l, Margin, _y, _fontH, _shapeB, false); _y += 22; }
                            break;
                        case PdfParagraph p:
                            foreach (string l in Wrap(p.Text, PageW - 2 * Margin, _fontR, _shapeR)) { Ensure(Lead); Text(l, Margin, _y, _fontR, _shapeR, false); _y += Lead; }
                            _y += 6;
                            break;
                        case PdfTable t:
                            Table(t);
                            _y += 6;
                            break;
                    }
                }
                Footer();
                _doc.EndPage();
                _doc.Close();
            }
            bytes = ms.ToArray();
            foreach (IDisposable d in new IDisposable[] { _shapeR, _shapeB, _fontR, _fontB, _fontH, _ink, _rule }) d.Dispose();
            return _page;
        }

        private void NewPage()
        {
            if (_canvas != null) { Footer(); _doc.EndPage(); }
            _page++;
            _canvas = _doc.BeginPage(PageW, PageH);
            _y = Margin;
        }

        private bool Ensure(float height)
        {
            if (_y + height <= PageH - Margin - 14) return false;
            NewPage();
            return true;
        }

        private void Footer()
        {
            float fy = PageH - Margin / 2;
            _canvas!.DrawLine(Margin, fy - 12, PageW - Margin, fy - 12, _rule);
            Text(model.FooterText ?? model.Title, Margin, fy, _fontR, _shapeR, false);
            string label = model.Locale == "bn" ? $"পৃষ্ঠা {_page} / {totalPages}" : $"Page {_page} of {totalPages}";
            Text(label, PageW - Margin, fy, _fontR, _shapeR, true);
        }

        private void Text(string text, float x, float baseline, SKFont font, SKShaper shaper, bool rightAlign)
        {
            if (text.Length == 0) return;
            SKShaper.Result r = shaper.Shape(text, font);
            using var builder = new SKTextBlobBuilder();
            var glyphs = r.Codepoints.Select(g => (ushort)g).ToArray();
            builder.AddPositionedRun(glyphs, font, r.Points);
            using SKTextBlob? blob = builder.Build();
            if (blob == null) return;
            _canvas!.DrawText(blob, rightAlign ? x - r.Width : x, baseline, _ink);
        }

        private static IEnumerable<string> Wrap(string text, float width, SKFont font, SKShaper shaper)
        {
            foreach (string para in text.Split('\n'))
            {
                string line = "";
                foreach (string word in para.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                {
                    string candidate = line.Length == 0 ? word : line + " " + word;
                    if (line.Length > 0 && shaper.Shape(candidate, font).Width > width) { yield return line; candidate = word; }
                    // A single token wider than the column (a long part number, a URL) is broken between grapheme
                    // clusters so it never spills into the next column.
                    while (shaper.Shape(candidate, font).Width > width && Split(candidate, width, font, shaper) is { } cut)
                    {
                        yield return candidate[..cut];
                        candidate = candidate[cut..];
                    }
                    line = candidate;
                }
                yield return line;
            }
        }

        /// <summary>Longest prefix (whole grapheme clusters, at least one) that fits; null when the text is one cluster.</summary>
        private static int? Split(string text, float width, SKFont font, SKShaper shaper)
        {
            var starts = new List<int>();   // extended grapheme clusters (UAX #29) on .NET 5+
            var e = System.Globalization.StringInfo.GetTextElementEnumerator(text);
            while (e.MoveNext()) starts.Add(e.ElementIndex);
            if (starts.Count < 2) return null;
            int cut = starts[1];
            for (int i = 2; i < starts.Count && shaper.Shape(text[..starts[i]], font).Width <= width; i++) cut = starts[i];
            return cut;
        }

        private void Table(PdfTable t)
        {
            int n = t.Header.Count;
            double[] w = t.ColumnWeights is { Count: var c } cw && c == n ? [.. cw] : [.. Enumerable.Repeat(1.0, n)];
            float[] colW = [.. w.Select(v => (float)(v / w.Sum()) * (PageW - 2 * Margin))];
            Ensure(2 * Lead + 2 * Pad);
            Row(t, colW, t.Header, true);
            foreach (var row in t.Rows) Row(t, colW, row, false);
        }

        private void Row(PdfTable t, float[] colW, IReadOnlyList<string> cells, bool header)
        {
            SKFont font = header ? _fontB : _fontR;
            SKShaper shaper = header ? _shapeB : _shapeR;
            var lines = new List<string>[colW.Length];
            for (int c = 0; c < colW.Length; c++) lines[c] = [.. Wrap(c < cells.Count ? cells[c] : "", colW[c] - 2 * Pad, font, shaper)];
            float h = lines.Max(l => l.Count) * Lead + 2 * Pad;
            if (Ensure(h) && !header) Row(t, colW, t.Header, true);   // repeat the header on a continuation page
            float x = Margin;
            for (int c = 0; c < colW.Length; c++)
            {
                float ly = _y + Pad + Lead - 4;
                foreach (string l in lines[c])
                {
                    if (c == t.MoneyColumn) Text(l, x + colW[c] - Pad, ly, font, shaper, true);
                    else Text(l, x + Pad, ly, font, shaper, false);
                    ly += Lead;
                }
                x += colW[c];
            }
            _y += h;
            _canvas!.DrawLine(Margin, _y, PageW - Margin, _y, _rule);
        }
    }
}
