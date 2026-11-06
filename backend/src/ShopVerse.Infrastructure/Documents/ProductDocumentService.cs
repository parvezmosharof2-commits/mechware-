using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Npgsql;
using ShopVerse.Application.Catalog;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Documents;

/// <summary>
/// Product documents (sprint 3–4). An upload is accepted only when every check passes, in this order, and nothing is kept
/// when one fails: the kind is known, the name's extension is one the kind allows, the size stays within the limit while it is
/// read (the declared length is not trusted), the first bytes say a type the kind allows and agree with the extension, and the
/// malware scan is clean. The stored name is chosen here; the client's name is only a cleaned label.
/// </summary>
public sealed class ProductDocumentService(
    ShopVerseDbContext db, IObjectStorage storage, IVirusScanner scanner, IOptions<DocumentOptions> options,
    ILogger<ProductDocumentService> logger) : IProductDocumentService
{
    private const int HeadLength = 16;

    public async Task<DocumentDto> UploadAsync(
        Guid productId, string? kind, string? fileName, Stream content, Guid? uploadedBy, CancellationToken ct = default)
    {
        var documentKind = ParseKind(kind);

        // Only whether it exists, here: the aggregate is loaded again, under a lock, right before it is changed (see below).
        if (!await db.Products.AnyAsync(p => p.Id == productId && p.DeletedAtUtc == null, ct))
            throw new KeyNotFoundException("The product does not exist.");

        string name = DocumentFileRules.CleanFileName(fileName);
        string extension = DocumentFileRules.ExtensionOf(name);
        // An empty or extension-less name has extension "", which no kind allows, so it is refused here too.
        if (!AllowedExtensions(documentKind).Contains(extension))
            throw new ValidationException("file", $"A {documentKind.ToString().ToLowerInvariant()} must be a file ending in {string.Join(", ", AllowedExtensions(documentKind).Select(e => "." + e))}.");

        long limit = LimitFor(documentKind);
        string tempPath = Path.Combine(Path.GetTempPath(), "mw-upload-" + Guid.NewGuid().ToString("N"));
        await using var temp = new FileStream(tempPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 81920,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        var (size, sha256, type) = await CopyCheckedAsync(content, temp, documentKind, extension, limit, ct);

        // The cheap refusals come before the expensive scan: a file the product already has, or a full product, would otherwise
        // tie the scanner up for nothing. They are repeated under the lock below, where they decide.
        await RefuseIfDuplicateOrFullAsync(productId, sha256, ct);

        temp.Position = 0;
        var scan = await scanner.ScanAsync(temp, ct);
        if (scan.Outcome == ScanOutcome.Unavailable)
        {
            logger.LogError("Malware scanner unavailable; upload for product {ProductId} refused", productId);
            throw new ServiceUnavailableException("SCAN_UNAVAILABLE", "The file could not be scanned, so it was not accepted. Try again later.");
        }

        if (scan.Outcome == ScanOutcome.Infected)
        {
            logger.LogWarning("Infected upload refused for product {ProductId}: {Signature}", productId, scan.Signature);
            throw new ValidationException("file", "The file contains malware and was not accepted.");
        }

        Guid id = Guid.NewGuid();
        string key = $"products/{productId:N}/{id:N}.{DocumentFileRules.Extensions(type)[0]}";

        // The scan can take seconds. The product is locked and loaded again only now, so the count of documents and the
        // duplicate check are made on what is there at this moment, and two uploads at once cannot both take the last place.
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        await db.Database.SqlQuery<int>($"SELECT 1 AS \"Value\" FROM products WHERE id = {productId} FOR UPDATE").ToListAsync(ct);
        var product = await db.Products.Include(p => p.Documents).FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAtUtc == null, ct)
                      ?? throw new KeyNotFoundException("The product does not exist.");
        // Added first: a duplicate or a full product is refused before anything is written to the store.
        var document = product.AddDocument(id, documentKind, name, type, size, sha256, key, uploadedBy);

        temp.Position = 0;
        await storage.PutAsync(key, temp, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await TryDeleteAsync(key);
            throw new ConflictException("DOCUMENT_EXISTS", "This exact file is already attached to the product.");
        }
        catch
        {
            await TryDeleteAsync(key);
            throw;
        }

        return ToDto(document);
    }

    private async Task RefuseIfDuplicateOrFullAsync(Guid productId, string sha256, CancellationToken ct)
    {
        if (await db.ProductDocuments.AnyAsync(d => d.ProductId == productId && d.Sha256 == sha256, ct))
            throw new ConflictException("DOCUMENT_EXISTS", "This exact file is already attached to the product.");
        if (await db.ProductDocuments.CountAsync(d => d.ProductId == productId, ct) >= Product.MaxDocuments)
            throw new ValidationException("file", $"A product can hold at most {Product.MaxDocuments} documents.");
    }

    public async Task<IReadOnlyList<DocumentDto>> ListAsync(Guid productId, CancellationToken ct = default)
    {
        var product = await db.Products.AsNoTracking().Include(p => p.Documents).FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAtUtc == null, ct)
                      ?? throw new KeyNotFoundException("The product does not exist.");
        return [.. product.Documents.OrderBy(d => d.CreatedAtUtc).ThenBy(d => d.FileName, StringComparer.Ordinal).Select(ToDto)];
    }

    public async Task DeleteAsync(Guid productId, Guid documentId, CancellationToken ct = default)
    {
        var product = await db.Products.Include(p => p.Documents).FirstOrDefaultAsync(p => p.Id == productId && p.DeletedAtUtc == null, ct)
                      ?? throw new KeyNotFoundException("The product does not exist.");
        var removed = product.RemoveDocument(documentId);
        await db.SaveChangesAsync(ct);
        await TryDeleteAsync(removed.StorageKey);   // the row is gone first: a failure here leaves an unreachable file, never a dead link
    }

    public async Task<DocumentDownload> OpenPublicAsync(string slug, Guid documentId, CancellationToken ct = default)
    {
        // The files of every product whose page is served, obsolete and end-of-life ones included: those are the parts people
        // most need a datasheet for. (Not Product.IsLinkable, which is about other pages sending buyers here.)
        var document = await (from p in db.Products.AsNoTracking().Where(p => p.Status == ProductStatus.Active && p.DeletedAtUtc == null)
                              where p.Slug == slug
                              from d in p.Documents
                              where d.Id == documentId
                              select d).FirstOrDefaultAsync(ct)
                       ?? throw new KeyNotFoundException("The document does not exist.");
        var stream = await storage.OpenReadAsync(document.StorageKey, ct);
        if (stream is null)
        {
            logger.LogError("Document {DocumentId} has no stored file at {Key}", document.Id, document.StorageKey);
            throw new KeyNotFoundException("The document does not exist.");
        }

        return new DocumentDownload(stream, document.FileName, document.ContentType, document.SizeBytes);
    }

    /// <summary>
    /// Exactly one of the three words. Enum.TryParse is not used: it accepts numbers, undefined values and comma lists
    /// ("datasheet,cad" is read as the two values or-ed together, which is "certificate").
    /// </summary>
    private static DocumentKind ParseKind(string? kind) => kind?.Trim().ToLowerInvariant() switch
    {
        "datasheet" => DocumentKind.Datasheet,
        "cad" => DocumentKind.Cad,
        "certificate" => DocumentKind.Certificate,
        _ => throw new ValidationException("kind", "The kind must be datasheet, cad or certificate.")
    };

    private long LimitFor(DocumentKind kind) => kind switch
    {
        DocumentKind.Datasheet => options.Value.MaxDatasheetBytes,
        DocumentKind.Cad => options.Value.MaxCadBytes,
        _ => options.Value.MaxCertificateBytes
    };

    private static IReadOnlyList<string> AllowedExtensions(DocumentKind kind) => kind switch
    {
        DocumentKind.Datasheet => DocumentFileRules.Extensions(DetectedFileType.Pdf),
        DocumentKind.Cad => DocumentFileRules.Extensions(DetectedFileType.Step),
        _ => [.. DocumentFileRules.Extensions(DetectedFileType.Pdf), .. DocumentFileRules.Extensions(DetectedFileType.Png), .. DocumentFileRules.Extensions(DetectedFileType.Jpeg)]
    };

    /// <summary>Copies to the temp file, counting what actually arrives; checks the type as soon as the first bytes are in.</summary>
    private static async Task<(long Size, string Sha256, DetectedFileType Type)> CopyCheckedAsync(
        Stream source, Stream destination, DocumentKind kind, string extension, long limit, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[81920];
        byte[] head = new byte[HeadLength];
        int headLength = 0;
        long total = 0;
        var type = DetectedFileType.Unknown;
        bool typeChecked = false;

        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            total += read;
            if (total > limit)
                throw new PayloadTooLargeException("FILE_TOO_LARGE", $"The file is larger than the {limit / (1024 * 1024)} MB limit for this kind of document.");

            if (headLength < HeadLength)
            {
                int take = Math.Min(read, HeadLength - headLength);
                Buffer.BlockCopy(buffer, 0, head, headLength, take);
                headLength += take;
            }

            if (!typeChecked && headLength == HeadLength)
            {
                type = CheckType(head.AsSpan(0, headLength), kind, extension);
                typeChecked = true;   // refused before the rest of a wrong file is even read
            }

            hash.AppendData(buffer, 0, read);
            await destination.WriteAsync(buffer.AsMemory(0, read), ct);
        }

        if (total == 0) throw new ValidationException("file", "The file is empty.");
        if (!typeChecked) type = CheckType(head.AsSpan(0, headLength), kind, extension);   // a file shorter than the head
        return (total, Convert.ToHexStringLower(hash.GetHashAndReset()), type);
    }

    private static DetectedFileType CheckType(ReadOnlySpan<byte> head, DocumentKind kind, string extension)
    {
        var type = DocumentFileRules.Detect(head);
        if (!DocumentFileRules.Allows(kind, type))
            throw new ValidationException("file", $"The file's content is not a type a {kind.ToString().ToLowerInvariant()} may be.");
        if (!DocumentFileRules.Extensions(type).Contains(extension))
            throw new ValidationException("file", "The file's name does not match what it contains.");
        return type;
    }

    private async Task TryDeleteAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not delete stored file {Key}", key);
        }
    }

    private static DocumentDto ToDto(ProductDocument d) =>
        new(d.Id, d.Kind.ToString().ToLowerInvariant(), d.FileName, d.ContentType, d.SizeBytes, d.Sha256, d.CreatedAtUtc);
}
