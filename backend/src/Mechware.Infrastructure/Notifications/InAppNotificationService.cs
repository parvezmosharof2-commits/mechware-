using System.Text.Json;
using Mechware.Application.Notifications;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;

namespace Mechware.Infrastructure.Notifications;

/// <summary>Reads and marks the caller's own in-app notices; every query filters by the caller's id, so other rows look like missing ones.</summary>
public sealed class InAppNotificationService(MechwareDbContext db, TimeProvider clock) : IInAppNotificationService
{
    public const int RetentionDays = 180;

    public async Task<InAppNotificationPage> ListAsync(Guid userId, bool unreadOnly, int page, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        const int size = IInAppNotificationService.MaxPageSize;
        var q = db.InAppNotifications.AsNoTracking().Where(n => n.UserId == userId);
        if (unreadOnly) q = q.Where(n => n.ReadAtUtc == null);
        var total = await q.CountAsync(ct);
        var rows = await q.OrderByDescending(n => n.CreatedAtUtc).ThenByDescending(n => n.Id).Skip((page - 1) * size).Take(size).ToListAsync(ct);
        return new InAppNotificationPage([.. rows.Select(ToDto)], page, size, total);
    }

    public Task<int> UnreadCountAsync(Guid userId, CancellationToken ct = default) =>
        db.InAppNotifications.AsNoTracking().CountAsync(n => n.UserId == userId && n.ReadAtUtc == null, ct);

    public async Task MarkReadAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        await db.InAppNotifications.Where(n => n.Id == id && n.UserId == userId && n.ReadAtUtc == null)
            .ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), ct);
        if (!await db.InAppNotifications.AsNoTracking().AnyAsync(n => n.Id == id && n.UserId == userId, ct))
            throw new KeyNotFoundException("Notification not found.");
    }

    public Task<int> MarkAllReadAsync(Guid userId, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        return db.InAppNotifications.Where(n => n.UserId == userId && n.ReadAtUtc == null).ExecuteUpdateAsync(s => s.SetProperty(n => n.ReadAtUtc, now), ct);
    }

    /// <summary>Deletes read notices read more than <see cref="RetentionDays"/> days ago; unread ones are never deleted.</summary>
    public Task<int> PurgeAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        var cutoff = nowUtc.AddDays(-RetentionDays);
        return db.InAppNotifications.Where(n => n.ReadAtUtc != null && n.ReadAtUtc < cutoff).ExecuteDeleteAsync(ct);
    }

    private static InAppNotificationDto ToDto(InAppNotification n) => new(n.Id, n.Kind, n.TitleKey,
        JsonSerializer.Deserialize<Dictionary<string, string>>(n.ParamsJson) ?? [], n.LinkPath, n.CreatedAtUtc, n.ReadAtUtc);
}
