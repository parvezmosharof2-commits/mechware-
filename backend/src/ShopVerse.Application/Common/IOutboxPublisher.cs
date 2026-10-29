namespace ShopVerse.Application.Common;

/// <summary>A serialized domain event leaving the outbox.</summary>
public sealed record OutboxEnvelope(Guid Id, string EventType, string AggregateType, Guid AggregateId, string Payload, DateTime OccurredAtUtc);

/// <summary>Port that delivers outbox messages to a downstream queue (Redis list → Meilisearch sync in Phase 5).</summary>
public interface IOutboxPublisher
{
    Task PublishAsync(OutboxEnvelope message, CancellationToken ct = default);
}
