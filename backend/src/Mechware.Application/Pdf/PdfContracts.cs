namespace Mechware.Application.Pdf;

/// <summary>A document to render. <see cref="Locale"/> is <c>en</c> or <c>bn</c> and picks the page-footer wording.</summary>
public sealed record PdfDocumentModel(string Title, string Locale, IReadOnlyList<PdfBlock> Blocks, string? FooterText = null, IReadOnlyList<string>? FooterLines = null);

public abstract record PdfBlock;

public sealed record PdfHeading(string Text) : PdfBlock;

public sealed record PdfParagraph(string Text) : PdfBlock;

/// <summary>
/// A table whose cells are already formatted text. <see cref="MoneyColumn"/> (0-based, -1 for none) is right-aligned;
/// <see cref="ColumnWeights"/> (optional, one per column) sets relative widths, default equal. The header repeats on each page.
/// </summary>
public sealed record PdfTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, int MoneyColumn = -1, IReadOnlyList<double>? ColumnWeights = null,
    bool Branded = false) : PdfBlock;

/// <summary>A full-width navy band (teal when <paramref name="Teal"/>) with a white title, a small line under it and right-aligned lines; an optional PNG/JPEG logo sits left of the title.</summary>
public sealed record PdfBand(string Title, string? Subtitle, IReadOnlyList<string> RightLines, byte[]? Logo = null, bool Teal = false) : PdfBlock;

/// <summary>A shaded box of label and value rows (a reference and date box). Text only; nothing in it is interpreted.</summary>
public sealed record PdfKeyValues(IReadOnlyList<KeyValuePair<string, string>> Rows) : PdfBlock;

/// <summary>Right-aligned label and amount rows; the last row is printed bold above a teal rule.</summary>
public sealed record PdfTotals(IReadOnlyList<KeyValuePair<string, string>> Rows) : PdfBlock;

/// <summary>The finished file, the lowercase hex SHA-256 of <see cref="Bytes"/>, and the page count.</summary>
public sealed record PdfResult(byte[] Bytes, string Sha256Hex, int PageCount);

/// <summary>
/// Renders a document to PDF. Output is deterministic: the same model always yields identical bytes (fixed dates and
/// producer, no random ids), so the hash can be stored as evidence of what was issued.
/// </summary>
public interface IPdfRenderer
{
    PdfResult Render(PdfDocumentModel model);
}
