using System.Text.RegularExpressions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Returns;

/// <summary>What an uploaded photo's first bytes say it is. The file name and the claimed content type are never asked.</summary>
public enum UploadedImageType { Unknown = 0, Jpeg = 1, Png = 2, WebP = 3 }

/// <summary>
/// F2.2 a buyer's photo of a return: at most <see cref="MaxPerReturn"/> per return (slots 1-5, unique per return), added while the
/// return is Requested or Approved, removed by the buyer only while it is Requested. What is kept is the server's own JPEG re-encoding
/// of the upload (no EXIF, GPS or other metadata); the key is chosen here, never by the client.
/// </summary>
public sealed partial class ReturnPhoto : Entity<Guid>
{
    public const int MaxPerReturn = 5;
    public const long MaxUploadBytes = 5L * 1024 * 1024;
    public const string ContentType = "image/jpeg";

    /// <summary>The bytes needed to tell the three types apart (WebP: "RIFF" + size + "WEBP").</summary>
    public const int HeadLength = 12;

    public Guid ReturnRequestId { get; private set; }

    /// <summary>1..<see cref="MaxPerReturn"/>: the smallest free one; the database's unique slot per return caps the count.</summary>
    public int Slot { get; private set; }

    /// <summary><c>uploads/rma/{returnId}/{random}.jpg</c>, built by <see cref="KeyFor"/>.</summary>
    public string StorageKey { get; private set; } = string.Empty;

    /// <summary>Size of the stored (re-encoded) file.</summary>
    public long SizeBytes { get; private set; }

    public int Width { get; private set; }
    public int Height { get; private set; }

    /// <summary>Lower-case hex SHA-256 of the stored file.</summary>
    public string Sha256 { get; private set; } = string.Empty;

    public Guid UploadedBy { get; private set; }

    private ReturnPhoto() { }

    public ReturnPhoto(Guid id, Guid returnRequestId, int slot, string storageKey, long sizeBytes, int width, int height, string sha256,
        Guid uploadedBy, DateTime nowUtc) : base(id)
    {
        if (slot is < 1 or > MaxPerReturn) throw new ValidationException("slot", $"A return holds at most {MaxPerReturn} photos.");
        if (!KeyPattern().IsMatch(storageKey) || !storageKey.StartsWith($"uploads/rma/{returnRequestId:N}/", StringComparison.Ordinal))
            throw new ValidationException("storageKey", "Not a return photo key.");
        if (sizeBytes <= 0 || width <= 0 || height <= 0) throw new ValidationException("file", "The photo is empty.");
        if (sha256.Length != 64) throw new ValidationException("sha256", "A SHA-256 hex digest is required.");
        ReturnRequestId = returnRequestId;
        Slot = slot;
        StorageKey = storageKey;
        SizeBytes = sizeBytes;
        Width = width;
        Height = height;
        Sha256 = sha256;
        UploadedBy = uploadedBy;
        CreatedAtUtc = nowUtc;
    }

    /// <summary>The key of a new photo: the return's id and <paramref name="random"/> (32 lower-case hex characters from the server).</summary>
    public static string KeyFor(Guid returnRequestId, string random)
    {
        if (random.Length != 32 || !random.All(c => c is (>= '0' and <= '9') or (>= 'a' and <= 'f')))
            throw new ArgumentException("The random part is 32 lower-case hex characters.", nameof(random));
        return $"uploads/rma/{returnRequestId:N}/{random}.jpg";
    }

    /// <summary>The same pattern as the database CHECK <c>ck_return_photos_key</c>.</summary>
    [GeneratedRegex("^uploads/rma/[0-9a-f]{32}/[0-9a-f]{32}\\.jpg$", RegexOptions.CultureInvariant)]
    public static partial Regex KeyPattern();

    /// <summary>Photos are added while staff have not received the goods yet.</summary>
    public static bool CanAdd(ReturnStatus status) => status is ReturnStatus.Requested or ReturnStatus.Approved;

    /// <summary>The buyer may take a photo back only before staff decided on the return.</summary>
    public static bool CanRemove(ReturnStatus status) => status == ReturnStatus.Requested;

    /// <summary>JPEG (FF D8 FF), PNG (its 8-byte signature) or WebP (RIFF....WEBP), from the first bytes only.</summary>
    public static UploadedImageType Detect(ReadOnlySpan<byte> head)
    {
        if (head.StartsWith(new byte[] { 0xFF, 0xD8, 0xFF })) return UploadedImageType.Jpeg;
        if (head.StartsWith(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A })) return UploadedImageType.Png;
        if (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8)) return UploadedImageType.WebP;
        return UploadedImageType.Unknown;
    }
}
