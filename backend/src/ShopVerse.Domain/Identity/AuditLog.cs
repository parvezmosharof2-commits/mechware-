using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Identity;

/// <summary>
/// Immutable audit trail record populated automatically by the EF Core SaveChangesInterceptor
/// or explicit security event logger.
/// </summary>
public sealed class AuditLog : Entity<Guid>
{
    public Guid? ActorId { get; private set; }
    public string? ActorIp { get; private set; }
    public string Action { get; private set; } = string.Empty;
    public string EntityName { get; private set; } = string.Empty;
    public string EntityId { get; private set; } = string.Empty;
    public string? OldValuesJson { get; private set; }
    public string? NewValuesJson { get; private set; }
    public string? CorrelationId { get; private set; }

    private AuditLog() { }

    public AuditLog(
        Guid id,
        Guid? actorId,
        string? actorIp,
        string action,
        string entityName,
        string entityId,
        string? oldValuesJson,
        string? newValuesJson,
        string? correlationId,
        DateTime createdAtUtc) : base(id)
    {
        ActorId = actorId;
        ActorIp = actorIp;
        Action = action;
        EntityName = entityName;
        EntityId = entityId;
        OldValuesJson = oldValuesJson;
        NewValuesJson = newValuesJson;
        CorrelationId = correlationId;
        CreatedAtUtc = createdAtUtc;
    }
}
