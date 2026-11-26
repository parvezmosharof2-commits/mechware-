using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.EntityFrameworkCore.Metadata;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Security;

namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// EF Core SaveChangesInterceptor automatically capturing ChangedEntities, OldValues, NewValues,
/// ActorId, IP, CorrelationId, and Timestamp into the AuditLog table (03_PHASE_PROMPTS.md §Phase 3 #7).
/// Automatically redacts sensitive secrets (PasswordHash, TwoFactorSecret, TokenHash).
/// <para>
/// A2.x (threat model v1, H4): a field-encrypted value is never copied out in clear, wherever it sits. The entity holds the plain
/// value and only the column's value converter encrypts it, so serialising the entity value would undo the encryption. Each
/// value is therefore checked in its column form: an encrypted column is written as <see cref="EncryptedMarker"/>, and a JSON
/// column with encrypted members (the quote company snapshot, the invoice buyer snapshot) is written in its stored shape with
/// every ciphertext replaced by the marker. Ciphertext is not copied either: the log is append-only, so a copy could never be
/// re-encrypted on a key rotation and would keep a retired key useful.
/// </para>
/// </summary>
public sealed class AuditLogSaveChangesInterceptor : SaveChangesInterceptor
{
    private static readonly HashSet<string> SensitiveProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        nameof(User.PasswordHash),
        nameof(User.TwoFactorSecret),
        nameof(User.TwoFactorRecoveryCodesJson),
        nameof(RefreshToken.TokenHash),
        nameof(OtpCode.CodeHash),
        // Business identifiers are encrypted in their own table (H4); the audit log must not copy them out in clear.
        nameof(Mechware.Domain.Companies.Company.Bin),
        nameof(Mechware.Domain.Companies.Company.Tin),
        nameof(Mechware.Domain.Companies.Company.TradeLicenceNo),
        CompanyBinIndex.Property
    };

    /// <summary>Written instead of a field-encrypted value (the column holds ciphertext; the log keeps neither it nor the plain value).</summary>
    public const string EncryptedMarker = "[encrypted]";

    private const string Redacted = "[REDACTED]";

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
            // Data Protection keys are key material (plain XML without a certificate): never copied into the audit log.
            if (entry.Entity is AuditLog or Microsoft.AspNetCore.DataProtection.EntityFrameworkCore.DataProtectionKey ||
                Attribute.IsDefined(entry.Entity.GetType(), typeof(NotAuditedAttribute)) ||
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

                if (entry.State == EntityState.Added)
                {
                    newValues[propName] = AuditValue(prop.Metadata, prop.CurrentValue);
                }
                else if (entry.State == EntityState.Deleted)
                {
                    oldValues[propName] = AuditValue(prop.Metadata, prop.OriginalValue);
                }
                else if (entry.State == EntityState.Modified && prop.IsModified)
                {
                    oldValues[propName] = AuditValue(prop.Metadata, prop.OriginalValue);
                    newValues[propName] = AuditValue(prop.Metadata, prop.CurrentValue);
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

    /// <summary>
    /// The value the audit row records for <paramref name="property"/>: a marker for secrets and field-encrypted values, the stored
    /// shape with each ciphertext masked for a JSON column holding encrypted members, the entity value otherwise.
    /// </summary>
    internal static object? AuditValue(IProperty property, object? value)
    {
        if (SensitiveProperties.Contains(property.Name)) return Redacted;
        if (value is null) return null;
        // Ciphertext held by the entity itself (the second-factor secrets): neither copied nor shown.
        if (value is string text && FieldProtector.IsProtected(text)) return EncryptedMarker;

        var converter = property.GetValueConverter() ?? property.FindTypeMapping()?.Converter;
        if (converter is null || converter.ProviderClrType != typeof(string) || converter.ModelClrType.IsEnum) return value;
        if (converter.ConvertToProvider(value) is not string stored || !stored.Contains(FieldProtector.Prefix, StringComparison.Ordinal))
            return value;
        if (FieldProtector.IsProtected(stored)) return EncryptedMarker; // an encrypted column (companies.bin, tin, trade_licence_no)

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(stored);
        }
        catch (JsonException)
        {
            return EncryptedMarker; // ciphertext inside a value that is not JSON: fail closed
        }

        return node is null ? EncryptedMarker : MaskCiphertext(node);
    }

    /// <summary>Replaces every string that is field ciphertext, at any depth, with <see cref="EncryptedMarker"/>.</summary>
    private static JsonNode MaskCiphertext(JsonNode node)
    {
        if (IsCiphertext(node)) return JsonValue.Create(EncryptedMarker);
        if (node is JsonObject obj)
        {
            foreach (string key in obj.Where(p => IsCiphertext(p.Value)).Select(p => p.Key).ToList()) obj[key] = EncryptedMarker;
            foreach (var (_, child) in obj) if (child is not null) MaskCiphertext(child);
        }
        else if (node is JsonArray array)
        {
            for (int i = 0; i < array.Count; i++)
                if (IsCiphertext(array[i])) array[i] = EncryptedMarker;
                else if (array[i] is { } item) MaskCiphertext(item);
        }

        return node;
    }

    private static bool IsCiphertext(JsonNode? node) =>
        node is JsonValue v && v.TryGetValue(out string? s) && FieldProtector.IsProtected(s);

    private static string ExtractPrimaryKey(EntityEntry entry)
    {
        PropertyEntry? idProp = entry.Properties.FirstOrDefault(p => p.Metadata.IsPrimaryKey());
        return idProp?.CurrentValue?.ToString() ?? "unknown";
    }
}
