using Mechware.Domain.Common.Primitives;

namespace Mechware.Infrastructure.Persistence.Outbox;

/// <summary>
/// One notification claimed for (outbox event, recipient, template). The unique index is the idempotency guard: a relay
/// that retries or runs twice cannot send the same email twice. Status: <c>sending</c> (claimed), <c>sent</c>, <c>failed</c> (permanent).
/// </summary>
[NotAudited]
public sealed class NotificationLogEntry
{
    public const string Sending = "sending";
    public const string Sent = "sent";
    public const string Failed = "failed";

    public Guid Id { get; set; }
    public Guid EventId { get; set; }
    public Guid RecipientUserId { get; set; }
    public string TemplateKey { get; set; } = string.Empty;
    public string Status { get; set; } = Sending;
    public string? Error { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime UpdatedAtUtc { get; set; }
}
