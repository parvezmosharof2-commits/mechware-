namespace Mechware.Application.Catalog;

public sealed record DocumentDto(Guid Id, string Kind, string FileName, string ContentType, long SizeBytes, string Sha256, DateTime CreatedAtUtc);

/// <summary>What a buyer sees on the product page: where to download it, never where it is stored.</summary>
public sealed record PublicDocumentDto(Guid Id, string Kind, string FileName, string ContentType, long SizeBytes, string Url);

/// <summary>A file opened for download. The caller disposes <see cref="Content"/>.</summary>
public sealed record DocumentDownload(Stream Content, string FileName, string ContentType, long SizeBytes);

public enum ScanOutcome { Clean = 1, Infected = 2, Unavailable = 3 }

/// <summary><c>Signature</c> names what was found when the file is infected.</summary>
public sealed record ScanResult(ScanOutcome Outcome, string? Signature = null);

/// <summary>Scans a file for malware. A scanner that cannot answer says <see cref="ScanOutcome.Unavailable"/>, never Clean.</summary>
public interface IVirusScanner
{
    Task<ScanResult> ScanAsync(Stream content, CancellationToken ct = default);
}

/// <summary>Where uploaded files are kept. Keys are chosen by the server.</summary>
public interface IObjectStorage
{
    Task PutAsync(string key, Stream content, CancellationToken ct = default);
    Task<Stream?> OpenReadAsync(string key, CancellationToken ct = default);
    Task DeleteAsync(string key, CancellationToken ct = default);
}

public interface IProductDocumentService
{
    /// <summary>
    /// Accepts a file only when its size is within the limit for its kind, its first bytes say a type the kind allows, its
    /// extension agrees, and the malware scan is clean. Otherwise nothing is stored.
    /// </summary>
    Task<DocumentDto> UploadAsync(Guid productId, string? kind, string? fileName, Stream content, Guid? uploadedBy, CancellationToken ct = default);

    Task<IReadOnlyList<DocumentDto>> ListAsync(Guid productId, CancellationToken ct = default);
    Task DeleteAsync(Guid productId, Guid documentId, CancellationToken ct = default);

    /// <summary>Opens a document of a published product by its slug; anything else is a not-found.</summary>
    Task<DocumentDownload> OpenPublicAsync(string slug, Guid documentId, CancellationToken ct = default);
}
