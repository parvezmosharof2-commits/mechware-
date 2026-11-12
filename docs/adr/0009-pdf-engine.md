# ADR 0009: PDF engine (English and Bangla)

## Status
Accepted for A8.1 / A8.2. One point is an owner decision only if QuestPDF is ever preferred (see Licence).

## Date
2026-11-13

## Context
Quotes (D4.7), invoices (E6.4) and RMA documents need PDFs in English and Bangla. Requirements: licence usable by a
small Bangladeshi B2B shop, correct Bangla shaping (conjuncts such as ক্ষ ন্ত স্ত্র ঙ্গ র‍্যা, reordering vowel signs
কি কে কৌ, reph and ya-phala), runs on Linux containers and Windows with no browser, fonts embedded with no network
fetch, byte-identical output for the same input.

## Options
| Option | Licence | Bangla shaping | Verdict |
|---|---|---|---|
| **SkiaSharp (PDF backend) + HarfBuzzSharp** | MIT (SkiaSharp, HarfBuzzSharp), BSD-3 (Skia), MIT (HarfBuzz) | Yes, HarfBuzz | **Chosen** |
| QuestPDF | "Community" licence free below USD 1M yearly gross revenue (and some other groups); above that a paid Professional/Enterprise licence. Terms from the vendor, not re-verified offline | Yes (Skia + HarfBuzz underneath) | Rejected as default: same engine, but adds a revenue condition and a licence key |
| PDFsharp / MigraDoc | MIT | No complex-script shaping (Bangla conjuncts would come out as separate glyphs) | Rejected |
| iText | AGPL or paid | Needs an add-on for Bangla | Rejected |
| Headless Chromium (Playwright) | Apache-2.0 | Yes | Rejected: needs a browser in the container |

## Decision
SkiaSharp with `SKDocument.CreatePdf` and `SkiaSharp.HarfBuzz` (`SKShaper`) shape each text run, build a glyph run, and
draw it. Packages (all MIT): `SkiaSharp`, `SkiaSharp.HarfBuzz`, `SkiaSharp.NativeAssets.Linux.NoDependencies` (no
fontconfig/freetype needed in the image), `HarfBuzzSharp.NativeAssets.Linux`; Windows natives ship in the base packages.
Test only: `PdfPig` (Apache-2.0) to read the PDF back.

The code is behind `IPdfRenderer` (Application), adapter `SkiaPdfRenderer` (Infrastructure), registered by
`AddPdfRendering()`. Swapping the engine later (for example QuestPDF) touches only the adapter.

## Licence and owner decision
Nothing in the chosen stack has a revenue threshold or a fee, so **no owner decision is needed** for it. Only if someone
later proposes QuestPDF would the owner have to accept its revenue-based licence (free below USD 1M, paid above).

## Font
Hind Siliguri Regular and Bold (SIL OFL 1.1, Indian Type Foundry) from google/fonts, stored as embedded resources in
`backend/src/Mechware.Infrastructure/Pdf/Fonts/` with `OFL.txt`. The frontend only carries Bengali-subset woff2 files,
which cannot be used here (no Latin, wrong format). One face covers both scripts. Nothing is fetched at runtime.
OFL allows embedding in documents; the font is not sold on its own.

## Bangla evidence
Unit tests (`backend/tests/Mechware.UnitTests/Pdf/PdfRendererTests.cs`) render conjunct samples and read the PDF back with
PdfPig: the glyph count in the file equals HarfBuzz's shaped count and is lower than the per-codepoint count (a naive
renderer would emit one glyph per codepoint, so the tests fail if shaping is off); the i-kar glyph precedes its
consonant in the run; the visual order of বাংলাদেশ (e-kar before দ) is correct. A page was also rasterised with PyMuPDF
for a visual check (not part of the suite).

## Determinism
Fixed creation/modification date (2000-01-01 UTC), producer and creator "Mechware", no random ids (Skia derives the
document id from the metadata), fixed layout, two-pass render so "page x of y" is exact. Same model gives identical
bytes and SHA-256 (tested). The hash is for evidence of what was issued; it is stable for one Skia build, so after a
SkiaSharp upgrade stored hashes of old files stay valid but a re-render may differ: store the file, not just the hash.

## Known limits
- This Skia build embeds the whole font face (compressed, about 120 KB per face used), not a subset. Acceptable for
  invoices; a pre-subset font file would shrink it if size ever matters.
- Skia writes the PDF text map (ToUnicode) from the font's cmap, so glyphs that exist only as joined forms (for example
  the ligature for ক্ষ) do not copy-paste back to Unicode; plain letters do. Search or copy of Bangla text from the PDF
  is therefore partial. Fine for printing and archiving; revisit if text search in PDFs becomes a requirement.
- Layout is deliberately simple: A4, heading, paragraph, table (repeating header, right-aligned money column), footer.
