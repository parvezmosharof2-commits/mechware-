namespace ShopVerse.Domain.Common.Primitives;

/// <summary>
/// Base record for domain events. Unlike the interface defaults on <see cref="IDomainEvent"/>,
/// the event id and timestamp are captured once so the outbox serializes stable values.
/// </summary>
public abstract record DomainEventBase : IDomainEvent
{
    public Guid EventId { get; init; } = Guid.NewGuid();
    public DateTime OccurredOnUtc { get; init; } = DateTime.UtcNow;
}
