using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Infrastructure.Persistence.Outbox;

/// <summary>
/// Moves pending aggregate domain events into <c>outbox_messages</c> inside the same SaveChanges
/// transaction, so state change and event are committed atomically (brief §6 principle 2).
/// Events are cleared as soon as their rows are tracked, so a retried SaveChanges cannot duplicate them.
/// </summary>
public sealed class DomainEventOutboxInterceptor : SaveChangesInterceptor
{
    private static readonly JsonSerializerOptions PayloadJson = new(JsonSerializerDefaults.Web);

    public override InterceptionResult<int> SavingChanges(DbContextEventData eventData, InterceptionResult<int> result)
    {
        if (eventData.Context is not null) CaptureEvents(eventData.Context);
        return base.SavingChanges(eventData, result);
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null) CaptureEvents(eventData.Context);
        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private static void CaptureEvents(DbContext context)
    {
        var aggregates = context.ChangeTracker.Entries()
            .Where(e => e.Entity is IHasDomainEvents { DomainEvents.Count: > 0 })
            .ToList();
        if (aggregates.Count == 0) return;

        var messages = new List<OutboxMessage>();
        foreach (var entry in aggregates)
        {
            var aggregate = (IHasDomainEvents)entry.Entity;
            var aggregateId = entry.Property("Id").CurrentValue is Guid id ? id : Guid.Empty;
            var aggregateType = entry.Entity.GetType().Name;

            foreach (var domainEvent in aggregate.DomainEvents)
            {
                var type = domainEvent.GetType();
                messages.Add(new OutboxMessage
                {
                    Id = (domainEvent as DomainEventBase)?.EventId ?? Guid.NewGuid(),
                    EventType = type.Name,
                    AggregateType = aggregateType,
                    AggregateId = aggregateId,
                    Payload = JsonSerializer.Serialize(domainEvent, type, PayloadJson),
                    OccurredAtUtc = (domainEvent as DomainEventBase)?.OccurredOnUtc ?? DateTime.UtcNow
                });
            }

            aggregate.ClearDomainEvents();
        }

        context.Set<OutboxMessage>().AddRange(messages);
    }
}
