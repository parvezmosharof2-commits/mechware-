using System.Security.Cryptography;
using System.Text;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Notifications;
using Mechware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mechware.Infrastructure.Quotes;

/// <summary>
/// One pass of the expiry notices (ADR-0011 §7, §14, D4.12). For a Sent version it queues, through the outbox, an "expiring" event once
/// the clock is within <see cref="Window"/> before <c>valid_until</c>, and an "expired" event once it is past it (for up to
/// <see cref="Window"/>, so a first deployment does not mail old history). The event id is a UUIDv8 of SHA-256(version id, kind): any
/// number of passes or Worker replicas insert the same row once (<c>ON CONFLICT DO NOTHING</c>), and the dispatcher's own log keeps
/// the mail itself once. It only reads quotes; it never changes a status, which stays computed from the clock.
/// </summary>
public sealed class QuoteNoticeService(MechwareDbContext db)
{
    public static readonly TimeSpan Window = TimeSpan.FromHours(48);

    /// <summary>Queues the notices due at <paramref name="nowUtc"/>; returns how many new events were written.</summary>
    public async Task<int> SweepAsync(DateTime nowUtc, int batch, CancellationToken ct = default)
    {
        var due = await db.QuoteVersions.AsNoTracking()
            .Where(v => v.Status == QuoteVersionStatus.Sent && v.ValidUntilUtc != null
                        && v.ValidUntilUtc > nowUtc - Window && v.ValidUntilUtc <= nowUtc + Window)
            .OrderBy(v => v.ValidUntilUtc).Take(batch)
            .Select(v => new { v.Id, v.QuoteId, v.VersionNo, ValidUntil = v.ValidUntilUtc!.Value }).ToListAsync(ct);

        var written = 0;
        foreach (var v in due)
        {
            var (kind, eventType) = nowUtc < v.ValidUntil ? ("quote.expiring", QuoteNotificationPublisher.ExpiringEvent) : ("quote.expired", QuoteNotificationPublisher.ExpiredEvent);
            var payload = $"{{\"quoteId\":\"{v.QuoteId}\",\"versionId\":\"{v.Id}\",\"versionNo\":{v.VersionNo}}}";
            written += await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO outbox_messages (id, event_type, aggregate_type, aggregate_id, payload, occurred_at, retry_count)
                VALUES ({EventId(v.Id, kind)}, {eventType}, 'Quote', {v.QuoteId}, {payload}::jsonb, {nowUtc}, 0)
                ON CONFLICT (id) DO NOTHING", ct);
        }
        return written;
    }

    /// <summary>UUIDv8 (RFC 9562, custom) from the first 16 bytes of SHA-256(version id, kind): the same input always gives the same id.</summary>
    public static Guid EventId(Guid versionId, string kind)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{versionId:N}|{kind}"));
        var b = hash.AsSpan(0, 16).ToArray();
        b[6] = (byte)((b[6] & 0x0F) | 0x80);
        b[8] = (byte)((b[8] & 0x3F) | 0x80);
        return new Guid(b, bigEndian: true);
    }
}
