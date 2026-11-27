using System.Text.Json;
using Mechware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mechware.Infrastructure.Notifications;

/// <summary>
/// Writes the in-app notice that goes with a buyer mail (F3c). One row per (outbox event, recipient): <c>INSERT ... ON CONFLICT DO NOTHING</c>,
/// so a retry of the event writes nothing twice. Only a translation key and plain values are stored; the web renders them in EN or BN.
/// </summary>
public sealed class InAppNotifier(MechwareDbContext db, StorefrontLinks links)
{
    /// <summary>Title key for a kind (the web has one EN and one BN string per key).</summary>
    public static string TitleKey(string kind) => "notifications." + kind.Replace('-', '_');

    /// <summary>The web path (starts with "/") for <paramref name="path"/> under the recipient's locale.</summary>
    public string Link(string locale, string path) => links.Path(locale, path);

    public async Task AddAsync(Guid eventId, Guid userId, string kind, string linkPath, IReadOnlyDictionary<string, string> values, CancellationToken ct = default)
    {
        var json = JsonSerializer.Serialize(values);
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO in_app_notifications (id, user_id, event_id, kind, title_key, params, link_path, created_at)
            VALUES ({Guid.NewGuid()}, {userId}, {eventId}, {kind}, {TitleKey(kind)}, {json}::jsonb, {linkPath}, {DateTime.UtcNow})
            ON CONFLICT (event_id, user_id) DO NOTHING", ct);
    }
}
