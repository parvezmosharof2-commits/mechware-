using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// F2.2 return photos through the C3 object store. An upload runs the cheap refusals first (own return, status, count), then reads at
/// most 5 MB into a temp file checking the first bytes (JPEG/PNG/WebP; the name and claimed type are never read), scans the upload as
/// it arrived (ClamAV; infected or unscannable is refused), re-encodes it (<see cref="ReturnPhotoImage"/>: no metadata survives) and,
/// under the return row's lock (FOR NO KEY UPDATE: the only lock taken, so it cannot deadlock with the order → return order of F2.1/F2.3),
/// checks status and count again, takes the smallest free slot, stores the JPEG under a key built here and commits with an audit row.
/// </summary>
public sealed class ReturnPhotoService(
    MechwareDbContext db, IObjectStorage storage, IVirusScanner scanner, ICurrentUserService user, TimeProvider clock,
    ILogger<ReturnPhotoService> logger) : IReturnPhotoService
{
    private const string Missing = "Not found.";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ReturnPhotoDto> UploadAsync(Guid userId, Guid returnId, Stream content, CancellationToken ct = default)
    {
        var status = await db.ReturnRequests.AsNoTracking().Where(r => r.Id == returnId && r.RequestedBy == userId)
            .Select(r => (ReturnStatus?)r.Status).FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException(Missing);
        RefuseIfClosed(status, adding: true);
        if (await db.ReturnPhotos.CountAsync(p => p.ReturnRequestId == returnId, ct) >= ReturnPhoto.MaxPerReturn) throw Full();

        byte[] upload = await ReadCheckedAsync(content, ct);

        using (var scanned = new MemoryStream(upload, writable: false))
        {
            var scan = await scanner.ScanAsync(scanned, ct);
            if (scan.Outcome == ScanOutcome.Unavailable)
            {
                logger.LogError("Malware scanner unavailable; photo for return {ReturnId} refused", returnId);
                throw new ServiceUnavailableException("SCAN_UNAVAILABLE", "The photo could not be scanned, so it was not accepted. Try again later.");
            }
            if (scan.Outcome == ScanOutcome.Infected)
            {
                logger.LogWarning("Infected return photo refused for return {ReturnId}: {Signature}", returnId,
                    scan.Signature?.Replace("\r", string.Empty).Replace("\n", string.Empty));
                throw new ValidationException("file", "The file contains malware and was not accepted.");
            }
        }

        var photo = ReturnPhotoImage.Reencode(upload);
        string sha256 = Convert.ToHexStringLower(SHA256.HashData(photo.Jpeg));
        string key = ReturnPhoto.KeyFor(returnId, RandomNumberGenerator.GetHexString(32, lowercase: true));
        var now = Now();

        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = (await db.Database.SqlQuery<string>(
                $"SELECT status AS \"Value\" FROM return_requests WHERE id = {returnId} AND requested_by = {userId} FOR NO KEY UPDATE").ToListAsync(ct))
            .SingleOrDefault() ?? throw new KeyNotFoundException(Missing);
        RefuseIfClosed(Enum.Parse<ReturnStatus>(locked), adding: true);
        var taken = await db.ReturnPhotos.Where(p => p.ReturnRequestId == returnId).Select(p => p.Slot).ToListAsync(ct);
        int slot = Enumerable.Range(1, ReturnPhoto.MaxPerReturn).Except(taken).Select(s => (int?)s).FirstOrDefault() ?? throw Full();

        var row = new ReturnPhoto(Guid.NewGuid(), returnId, slot, key, photo.Jpeg.Length, photo.Width, photo.Height, sha256, userId, now);
        db.ReturnPhotos.Add(row);
        db.AuditLogs.Add(Audit("return.photo_added", row, now));
        using (var stored = new MemoryStream(photo.Jpeg, writable: false))
            await storage.PutAsync(key, stored, ct);
        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            await TryDeleteAsync(key);
            throw Full();
        }
        catch
        {
            await TryDeleteAsync(key);
            throw;
        }

        return ToDto(row);
    }

    public async Task<IReadOnlyList<ReturnPhotoDto>> ListOwnAsync(Guid userId, Guid returnId, CancellationToken ct = default)
    {
        if (!await db.ReturnRequests.AnyAsync(r => r.Id == returnId && r.RequestedBy == userId, ct)) throw new KeyNotFoundException(Missing);
        return await ListAsync(returnId, ct);
    }

    public async Task<IReadOnlyList<ReturnPhotoDto>> ListStaffAsync(Guid returnId, CancellationToken ct = default)
    {
        if (!await db.ReturnRequests.AnyAsync(r => r.Id == returnId, ct)) throw new KeyNotFoundException(Missing);
        return await ListAsync(returnId, ct);
    }

    public Task<ReturnPhotoFile> OpenOwnAsync(Guid userId, Guid returnId, Guid photoId, CancellationToken ct = default) =>
        OpenAsync(from p in db.ReturnPhotos
                  join r in db.ReturnRequests on p.ReturnRequestId equals r.Id
                  where p.Id == photoId && r.Id == returnId && r.RequestedBy == userId
                  select p, ct);

    public Task<ReturnPhotoFile> OpenStaffAsync(Guid returnId, Guid photoId, CancellationToken ct = default) =>
        OpenAsync(db.ReturnPhotos.Where(p => p.Id == photoId && p.ReturnRequestId == returnId), ct);

    public async Task DeleteOwnAsync(Guid userId, Guid returnId, Guid photoId, CancellationToken ct = default)
    {
        var now = Now();
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var locked = (await db.Database.SqlQuery<string>(
                $"SELECT status AS \"Value\" FROM return_requests WHERE id = {returnId} AND requested_by = {userId} FOR NO KEY UPDATE").ToListAsync(ct))
            .SingleOrDefault() ?? throw new KeyNotFoundException(Missing);
        var photo = await db.ReturnPhotos.FirstOrDefaultAsync(p => p.Id == photoId && p.ReturnRequestId == returnId, ct)
                    ?? throw new KeyNotFoundException(Missing);
        RefuseIfClosed(Enum.Parse<ReturnStatus>(locked), adding: false);
        db.ReturnPhotos.Remove(photo);
        db.AuditLogs.Add(Audit("return.photo_removed", photo, now, removed: true));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        await TryDeleteAsync(photo.StorageKey);   // the row is gone first: a failure leaves an unreachable file, never a dead link
    }

    /// <summary>Counts what arrives (the declared length is not trusted) and checks the first bytes as soon as they are in.</summary>
    private static async Task<byte[]> ReadCheckedAsync(Stream source, CancellationToken ct)
    {
        using var buffer = new MemoryStream();
        byte[] chunk = new byte[81920];
        bool typeChecked = false;
        int read;
        while ((read = await source.ReadAsync(chunk, ct)) > 0)
        {
            if (buffer.Length + read > ReturnPhoto.MaxUploadBytes)
                throw new PayloadTooLargeException("FILE_TOO_LARGE", $"A photo may be at most {ReturnPhoto.MaxUploadBytes / (1024 * 1024)} MB.");
            buffer.Write(chunk, 0, read);
            if (!typeChecked && buffer.Length >= ReturnPhoto.HeadLength)
            {
                CheckType(buffer.GetBuffer().AsSpan(0, ReturnPhoto.HeadLength));   // a wrong file is refused before the rest is read
                typeChecked = true;
            }
        }

        if (buffer.Length == 0) throw new ValidationException("file", "The file is empty.");
        if (!typeChecked) CheckType(buffer.GetBuffer().AsSpan(0, (int)buffer.Length));
        return buffer.ToArray();
    }

    private static void CheckType(ReadOnlySpan<byte> head)
    {
        if (ReturnPhoto.Detect(head) == UploadedImageType.Unknown)
            throw new ValidationException("file", "Only JPEG, PNG or WebP photos are accepted.");
    }

    private static void RefuseIfClosed(ReturnStatus status, bool adding)
    {
        if (adding ? !ReturnPhoto.CanAdd(status) : !ReturnPhoto.CanRemove(status))
            throw new ConflictException("RETURN_PHOTOS_CLOSED", adding
                ? "Photos can be added only until the goods are received."
                : "A photo can be removed only before the return is decided.");
    }

    private static ConflictException Full() =>
        new("RETURN_PHOTOS_FULL", $"A return holds at most {ReturnPhoto.MaxPerReturn} photos. Remove one first.");

    private async Task<IReadOnlyList<ReturnPhotoDto>> ListAsync(Guid returnId, CancellationToken ct) =>
        [.. (await db.ReturnPhotos.AsNoTracking().Where(p => p.ReturnRequestId == returnId).OrderBy(p => p.Slot).ToListAsync(ct)).Select(ToDto)];

    private async Task<ReturnPhotoFile> OpenAsync(IQueryable<ReturnPhoto> query, CancellationToken ct)
    {
        var photo = await query.AsNoTracking().FirstOrDefaultAsync(ct) ?? throw new KeyNotFoundException(Missing);
        var stream = await storage.OpenReadAsync(photo.StorageKey, ct);
        if (stream is null)
        {
            logger.LogError("Return photo {PhotoId} has no stored file", photo.Id);
            throw new KeyNotFoundException(Missing);
        }
        return new ReturnPhotoFile(photo.Id, stream, ReturnPhoto.ContentType, photo.SizeBytes);
    }

    private async Task TryDeleteAsync(string key)
    {
        try
        {
            await storage.DeleteAsync(key);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not delete stored return photo {Key}", key);
        }
    }

    /// <summary>The clock at the database's microsecond precision.</summary>
    private DateTime Now()
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return now.AddTicks(-(now.Ticks % 10));
    }

    /// <summary>On the return (entity <c>return_requests</c>): the photo's id, slot, size and fingerprint; added → new values, removed → old.</summary>
    private AuditLog Audit(string action, ReturnPhoto p, DateTime now, bool removed = false)
    {
        string values = JsonSerializer.Serialize(new { PhotoId = p.Id, p.Slot, p.SizeBytes, p.Width, p.Height, p.Sha256 }, Json);
        return new(Guid.NewGuid(), user.UserId ?? p.UploadedBy, user.IpAddress, action, "return_requests", p.ReturnRequestId.ToString(),
            removed ? values : null, removed ? null : values, user.CorrelationId, now);
    }

    private static ReturnPhotoDto ToDto(ReturnPhoto p) =>
        new(p.Id, p.Slot, ReturnPhoto.ContentType, p.SizeBytes, p.Width, p.Height, p.CreatedAtUtc);
}
