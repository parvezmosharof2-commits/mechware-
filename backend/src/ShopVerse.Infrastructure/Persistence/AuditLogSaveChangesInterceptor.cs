using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Infrastructure.Persistence;

/// <summary>
/// EF Core SaveChangesInterceptor automatically capturing ChangedEntities, OldValues, NewValues,
/// ActorId, IP, CorrelationId, and Timestamp into the AuditLog table (03_PHASE_PROMPTS.md §Phase 3 #7).
/// Automatically redacts sensitive secrets (PasswordHash, TwoFactorSecret, TokenHash).
/// </summary>
public sealed class AuditLogSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(User.PasswordHash),
        nameof(User.TwoFactorSecret),
        nameof(User.TwoFactorRecoveryCodesJson),
        nameof(RefreshToken.TokenHash),
        nameof(OtpCode.CodeHash)
    };

    private readonly ICurrentUserService _currentUserService;

    public AuditLogSaveChangesInterceptor(ICurrentUserService currentUserService)
    {
        _currentUserService = currentUserService;
    }

    public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
        DbContextEventData eventData,
        InterceptionResult<int> result,
        CancellationToken cancellationToken = default)
    {
        if (eventData.Context is not null)
        {
            CaptureAuditEntries(eventData.Context);
        }

        return base.SavingChangesAsync(eventData, result, cancellationToken);
    }

    private void CaptureAuditEntries(DbContext context)
    {
        context.ChangeTracker.DetectChanges();

        var now = DateTime.UtcNow;
        var auditLogs = new List<AuditLog>();

        foreach (EntityEntry entry in context.ChangeTracker.Entries().ToList())
        {
            if (entry.Entity is AuditLog ||
                entry.State is EntityState.Detached or EntityState.Unchanged)
            {
                continue;
            }

            string entityName = entry.Metadata.GetTableName() ?? entry.Entity.GetType().Name;
            string action = entry.State switch
            {
                EntityState.Added => $"{entityName}.created",
                EntityState.Modified => $"{entityName}.updated",
                EntityState.Deleted => $"{entityName}.deleted",
                _ => $"{entityName}.changed"
            };

            string entityId = ExtractPrimaryKey(entry);
            var oldValues = new Dictionary<string, object?>();
            var newValues = new Dictionary<string, object?>();

            foreach (PropertyEntry prop in entry.Properties)
            {
                string propName = prop.Metadata.Name;
                bool isSensitive = SensitiveProperties.Contains(propName);

                if (entry.State == EntityState.Added)
                {
                    newValues[propName] = isSensitive ? "[REDACTED]" : prop.CurrentValue;
                }
                else if (entry.State == EntityState.Deleted)
                {
                    oldValues[propName] = isSensitive ? "[REDACTED]" : prop.OriginalValue;
                }
                else if (entry.State == EntityState.Modified && prop.IsModified)
                {
                    oldValues[propName] = isSensitive ? "[REDACTED]" : prop.OriginalValue;
                    newValues[propName] = isSensitive ? "[REDACTED]" : prop.CurrentValue;
                }
            }

            auditLogs.Add(new AuditLog(
                Guid.NewGuid(),
                _currentUserService.UserId,
                _currentUserService.IpAddress,
                action,
                entityName,
                entityId,
                oldValues.Count > 0 ? JsonSerializer.Serialize(oldValues) : null,
                newValues.Count > 0 ? JsonSerializer.Serialize(newValues) : null,
                _currentUserService.CorrelationId,
                now));
        }

        if (auditLogs.Count > 0)
        {
            context.Set<AuditLog>().AddRange(auditLogs);
        }
    }

    private static string ExtractPrimaryKey(EntityEntry entry)
    {
        PropertyEntry? idProp = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey());
        return idProp?.CurrentValue?.ToString() ?? "unknown";
    }
}
