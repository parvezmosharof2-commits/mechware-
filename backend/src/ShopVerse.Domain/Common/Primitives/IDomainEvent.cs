namespace ShopVerse.Domain.Common.Primitives;

/// <summary>
/// Marker interface for Domain Events published within the transactional outbox boundary.
/// </summary>
public interface IDomainEvent
{
    Guid EventId => Guid.NewGuid();
    DateTime OccurredOnUtc => DateTime.UtcNow;
}
