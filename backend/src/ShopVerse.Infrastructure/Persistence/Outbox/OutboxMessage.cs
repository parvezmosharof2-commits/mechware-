using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Infrastructure.Persistence.Outbox;

/// <summary>Transactional-outbox row written in the same transaction as the state change (DATA_MODEL §2.9).</summary>
[NotAudited]
public sealed class OutboxMessage
{
    public Guid Id { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string AggregateType { get; set; } = string.Empty;
    public Guid AggregateId { get; set; }
    public string Payload { get; set; } = "{}";
    public DateTime OccurredAtUtc { get; set; }
    public DateTime? ProcessedAtUtc { get; set; }
    public int RetryCount { get; set; }
    public string? Error { get; set; }
}
