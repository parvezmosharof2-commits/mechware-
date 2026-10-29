namespace ShopVerse.Domain.Common.Primitives;

/// <summary>Non-generic view of an aggregate's pending events, used by the outbox interceptor.</summary>
public interface IHasDomainEvents
{
    IReadOnlyCollection<IDomainEvent> DomainEvents { get; }
    void ClearDomainEvents();
}

/// <summary>
/// Marks high-frequency, system-driven entities (stock holds, outbox rows) that must not flood the
/// admin/seller <c>audit_logs</c> trail.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public sealed class NotAuditedAttribute : Attribute;
