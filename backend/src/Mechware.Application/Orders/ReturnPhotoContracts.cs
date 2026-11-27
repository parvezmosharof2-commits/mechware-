namespace Mechware.Application.Orders;

/// <summary>A return photo as the buyer and staff see it. Never where it is stored, never the client's file name.</summary>
public sealed record ReturnPhotoDto(Guid Id, int Slot, string ContentType, long SizeBytes, int Width, int Height, DateTime CreatedAtUtc);

/// <summary>A photo opened for viewing; the caller disposes <see cref="Content"/>.</summary>
public sealed record ReturnPhotoFile(Guid Id, Stream Content, string ContentType, long SizeBytes);

/// <summary>
/// F2.2 return photos. Every miss (another buyer's return, an unknown return or photo, a photo of another return, a lost file) is the
/// same not-found, so a caller learns nothing about what they may not see.
/// </summary>
public interface IReturnPhotoService
{
    /// <summary>
    /// The buyer's own return, Requested or Approved (else 409 <c>RETURN_PHOTOS_CLOSED</c>), fewer than five photos (else 409
    /// <c>RETURN_PHOTOS_FULL</c>). Checks in order: size while reading (413 <c>FILE_TOO_LARGE</c>), JPEG/PNG/WebP by the first bytes
    /// (400), the malware scan (400 infected, 503 <c>SCAN_UNAVAILABLE</c>), a decodable picture (400); then the server's JPEG
    /// re-encoding (no metadata) is stored under a key it builds.
    /// </summary>
    Task<ReturnPhotoDto> UploadAsync(Guid userId, Guid returnId, Stream content, CancellationToken ct = default);

    Task<IReadOnlyList<ReturnPhotoDto>> ListOwnAsync(Guid userId, Guid returnId, CancellationToken ct = default);
    Task<ReturnPhotoFile> OpenOwnAsync(Guid userId, Guid returnId, Guid photoId, CancellationToken ct = default);

    /// <summary>Only while the return is Requested (else 409 <c>RETURN_PHOTOS_CLOSED</c>); staff have no delete.</summary>
    Task DeleteOwnAsync(Guid userId, Guid returnId, Guid photoId, CancellationToken ct = default);

    Task<IReadOnlyList<ReturnPhotoDto>> ListStaffAsync(Guid returnId, CancellationToken ct = default);
    Task<ReturnPhotoFile> OpenStaffAsync(Guid returnId, Guid photoId, CancellationToken ct = default);
}
