using Mechware.Domain.Common.Primitives;

namespace Mechware.Infrastructure.Persistence.Outbox;

/// <summary>
/// An in-app notice for one user (F3c). Text is a translation key plus JSON values, never HTML. The unique (event_id, user_id) index is the
/// idempotency guard, so an outbox retry cannot write a second row. Not audit data: read rows are deleted after 180 days.
/// </summary>
[NotAudited]
public sealed class InAppNotification
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid EventId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string TitleKey { get; set; } = string.Empty;
    public string ParamsJson { get; set; } = "{}";
    public string LinkPath { get; set; } = "/";
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? ReadAtUtc { get; set; }
}
