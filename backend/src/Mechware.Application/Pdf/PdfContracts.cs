namespace Mechware.Application.Pdf;

/// <summary>A document to render. <see cref="Locale"/> is <c>en</c> or <c>bn</c> and picks the page-footer wording.</summary>
public sealed record PdfDocumentModel(string Title, string Locale, IReadOnlyList<PdfBlock> Blocks, string? FooterText = null);

public abstract record PdfBlock;

public sealed record PdfHeading(string Text) : PdfBlock;

public sealed record PdfParagraph(string Text) : PdfBlock;

/// <summary>
/// A table whose cells are already formatted text. <see cref="MoneyColumn"/> (0-based, -1 for none) is right-aligned;
/// <see cref="ColumnWeights"/> (optional, one per column) sets relative widths, default equal. The header repeats on each page.
/// </summary>
public sealed record PdfTable(IReadOnlyList<string> Header, IReadOnlyList<IReadOnlyList<string>> Rows, int MoneyColumn = -1, IReadOnlyList<double>? ColumnWeights = null) : PdfBlock;

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
