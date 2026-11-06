using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Catalog;

/// <summary>What a product document is for; it decides which file types are accepted.</summary>
public enum DocumentKind { Datasheet = 1, Cad = 2, Certificate = 3 }

/// <summary>The type of a file, decided from its first bytes, never from its name or the content type the client claims.</summary>
public enum DetectedFileType { Unknown = 0, Pdf = 1, Step = 2, Png = 3, Jpeg = 4 }

/// <summary>
/// Which files a document may be (sprint 3–4, upload abuse gate). The type is read from the first bytes and must be one the
/// kind allows, and the file name's extension must say the same thing, so an executable renamed <c>.pdf</c> is refused.
/// The stored content type is ours, taken from the detected type.
/// </summary>
public static class DocumentFileRules
{
    public static DetectedFileType Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith("%PDF-"u8)) return DetectedFileType.Pdf;
        if (head.StartsWith("ISO-10303-21;"u8)) return DetectedFileType.Step;
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return DetectedFileType.Png;
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return DetectedFileType.Jpeg;
        return DetectedFileType.Unknown;
    }

    public static bool Allows(DocumentKind kind, DetectedFileType type) => (kind, type) switch
    {
        (DocumentKind.Datasheet, DetectedFileType.Pdf) => true,
        (DocumentKind.Cad, DetectedFileType.Step) => true,
        (DocumentKind.Certificate, DetectedFileType.Pdf or DetectedFileType.Png or DetectedFileType.Jpeg) => true,
        _ => false
    };

    /// <summary>The extensions (lower case, no dot) a file of this type may carry.</summary>
    public static IReadOnlyList<string> Extensions(DetectedFileType type) => type switch
    {
        DetectedFileType.Pdf => ["pdf"],
        DetectedFileType.Step => ["step", "stp"],
        DetectedFileType.Png => ["png"],
        DetectedFileType.Jpeg => ["jpg", "jpeg"],
        _ => []
    };

    public static string ContentType(DetectedFileType type) => type switch
    {
        DetectedFileType.Pdf => "application/pdf",
        DetectedFileType.Step => "model/step",
        DetectedFileType.Png => "image/png",
        DetectedFileType.Jpeg => "image/jpeg",
        _ => "application/octet-stream"
    };

    /// <summary>
    /// A name safe to show and to put in a header: no directories, no control characters, at most 150 characters. It is
    /// only a label: the stored file is named by the server.
    /// </summary>
    public static string CleanFileName(string? raw)
    {
        string name = (raw ?? string.Empty).Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        name = new string([.. name.Where(c => !char.IsControl(c) && c is not ('"' or '<' or '>' or '|' or ':' or '*' or '?'))]).Trim().Trim('.');
        if (name.Length > 150)
        {
            string ext = Path.GetExtension(name);
            name = name[..(150 - Math.Min(ext.Length, 20))] + (ext.Length <= 20 ? ext : string.Empty);
        }

        return name;
    }

    public static string ExtensionOf(string cleanName) => Path.GetExtension(cleanName).TrimStart('.').ToLowerInvariant();
}

/// <summary>A scanned, stored file belonging to a product (datasheet, CAD model, certificate).</summary>
public sealed class ProductDocument : Entity<Guid>
{
    public Guid ProductId { get; private set; }
    public DocumentKind Kind { get; private set; }

    /// <summary>The name to show, cleaned (see <see cref="DocumentFileRules.CleanFileName"/>); not where the file is kept.</summary>
    public string FileName { get; private set; } = string.Empty;

    public string ContentType { get; private set; } = string.Empty;
    public long SizeBytes { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the file; the same file is not stored twice for one product.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    /// <summary>Where the object store keeps it. Chosen by the server, never by the client.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    public Guid? UploadedBy { get; private set; }

    private ProductDocument() { }

    internal ProductDocument(Guid id, Guid productId, DocumentKind kind, string fileName, DetectedFileType type, long sizeBytes,
        string sha256, string storageKey, Guid? uploadedBy) : base(id)
    {
        if (!Enum.IsDefined(kind)) throw new ValidationException("kind", "Unknown document kind.");
        if (!DocumentFileRules.Allows(kind, type))
            throw new ValidationException("file", $"A {kind.ToString().ToLowerInvariant()} cannot be a {type} file.");
        if (string.IsNullOrWhiteSpace(fileName)) throw new ValidationException("file", "The file needs a name.");
        if (sizeBytes <= 0) throw new ValidationException("file", "The file is empty.");
        if (sha256.Length != 64) throw new ValidationException("sha256", "A SHA-256 hex digest is required.");
        if (string.IsNullOrWhiteSpace(storageKey)) throw new ValidationException("storageKey", "A storage key is required.");

        ProductId = productId;
        Kind = kind;
        FileName = fileName;
        ContentType = DocumentFileRules.ContentType(type);
        SizeBytes = sizeBytes;
        Sha256 = sha256;
        StorageKey = storageKey;
        UploadedBy = uploadedBy;
    }
}
