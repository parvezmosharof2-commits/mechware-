using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Audit;
using Mechware.Application.Catalog;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;

namespace Mechware.Infrastructure.Persistence;

/// <summary>A row of <c>audit_logs</c> as the viewer reads it: the chain position is a column outside the EF model (AuditChainSql).</summary>
internal sealed class AuditRow
{
    public Guid Id { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public Guid? ActorId { get; init; }
    public string? ActorIp { get; init; }
    public string Action { get; init; } = "";
    public string EntityName { get; init; } = "";
    public string EntityId { get; init; } = "";
    public string? CorrelationId { get; init; }
    public long? ChainPosition { get; init; }
    public string? OldValues { get; init; }
    public string? NewValues { get; init; }
}

/// <summary>
/// The audit list and detail (G5.2b, audit.view). Values are redacted by <see cref="AuditRedaction"/> and never decrypted; every read
/// writes its own audit row (who looked, at what) after the read, so a list never shows the row of its own request.
/// </summary>
public sealed class AuditLogReadService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock) : IAuditLogReadService
{
    private static readonly TimeSpan Dhaka = TimeSpan.FromHours(6);
    private const int MaxTextLength = 100;

    private IQueryable<AuditRow> Rows(bool withValues) => withValues
        ? db.Database.SqlQuery<AuditRow>($"""
            SELECT id AS "Id", created_at AS "CreatedAtUtc", actor_id AS "ActorId", actor_ip AS "ActorIp", action AS "Action",
                   entity_name AS "EntityName", entity_id AS "EntityId", correlation_id AS "CorrelationId", chain_position AS "ChainPosition",
                   old_values::text AS "OldValues", new_values::text AS "NewValues"
            FROM audit_logs
            """)
        : db.Database.SqlQuery<AuditRow>($"""
            SELECT id AS "Id", created_at AS "CreatedAtUtc", actor_id AS "ActorId", actor_ip AS "ActorIp", action AS "Action",
                   entity_name AS "EntityName", entity_id AS "EntityId", correlation_id AS "CorrelationId", chain_position AS "ChainPosition",
                   NULL::text AS "OldValues", NULL::text AS "NewValues"
            FROM audit_logs
            """);

    public async Task<PagedResult<AuditLogListItemDto>> ListAsync(AuditLogFilter f, CancellationToken ct = default)
    {
        var action = Text(f.Action, "action");
        var entityName = Text(f.EntityName, "entityName");
        var entityId = Text(f.EntityId, "entityId");
        var correlationId = Text(f.CorrelationId, "correlationId");
        var (fromUtc, toUtc) = Range(f.From, f.To);
        var page = Math.Max(1, f.Page);
        var pageSize = Math.Clamp(f.PageSize, 1, IAuditLogReadService.MaxPageSize);

        var result = await ReadSnapshot.RunAsync(db, async () =>
        {
            var q = Rows(withValues: false);
            if (f.ActorId is { } actor) q = q.Where(r => r.ActorId == actor);
            if (action is not null) q = q.Where(r => r.Action == action);
            if (entityName is not null) q = q.Where(r => r.EntityName == entityName);
            if (entityId is not null) q = q.Where(r => r.EntityId == entityId);
            if (correlationId is not null) q = q.Where(r => r.CorrelationId == correlationId);
            if (fromUtc is { } from) q = q.Where(r => r.CreatedAtUtc >= from);
            if (toUtc is { } to) q = q.Where(r => r.CreatedAtUtc < to);

            var total = await q.CountAsync(ct);
            var rows = await q.OrderByDescending(r => r.CreatedAtUtc).ThenByDescending(r => r.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
            var emails = await EmailsAsync(rows, ct);
            var items = rows.Select(r => new AuditLogListItemDto(r.Id, r.CreatedAtUtc, r.ActorId, Email(emails, r.ActorId), r.ActorIp, r.Action,
                r.EntityName, r.EntityId, r.CorrelationId, r.ChainPosition)).ToList();
            return new PagedResult<AuditLogListItemDto>(items, page, pageSize, total);
        }, ct);

        await RecordAsync(IAuditLogReadService.SearchedAction, "-", new
        {
            ActorId = f.ActorId,
            Action = action,
            EntityName = entityName,
            EntityId = entityId,
            CorrelationId = correlationId,
            From = f.From,
            To = f.To,
            Page = page,
            PageSize = pageSize,
            result.TotalCount
        }, ct);
        return result;
    }

    public async Task<AuditLogDetailDto> GetAsync(Guid id, CancellationToken ct = default)
    {
        var r = await Rows(withValues: true).Where(x => x.Id == id).SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException("Audit entry not found.");
        var emails = await EmailsAsync([r], ct);
        var detail = new AuditLogDetailDto(r.Id, r.CreatedAtUtc, r.ActorId, Email(emails, r.ActorId), r.ActorIp, r.Action, r.EntityName, r.EntityId,
            r.CorrelationId, r.ChainPosition, AuditRedaction.Redact(r.OldValues), AuditRedaction.Redact(r.NewValues));
        await RecordAsync(IAuditLogReadService.ViewedAction, id.ToString(), new { r.Action, r.EntityName, r.EntityId }, ct);
        return detail;
    }

    private async Task<Dictionary<Guid, string>> EmailsAsync(IReadOnlyCollection<AuditRow> rows, CancellationToken ct)
    {
        var ids = rows.Where(r => r.ActorId is not null).Select(r => r.ActorId!.Value).Distinct().ToList();
        return ids.Count == 0 ? [] : await db.Users.AsNoTracking().Where(u => ids.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.Email, ct);
    }

    private static string? Email(Dictionary<Guid, string> emails, Guid? actor) => actor is { } a ? emails.GetValueOrDefault(a) : null;

    /// <summary>Reading the audit log is itself audited (threat model row of G5.2b): who, from where, which filters or row.</summary>
    private async Task RecordAsync(string action, string entityId, object details, CancellationToken ct)
    {
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), user.UserId, user.IpAddress, action, "audit_logs", entityId, null,
            JsonSerializer.Serialize(details), user.CorrelationId, clock.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(ct);
    }

    private static string? Text(string? value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var t = value.Trim();
        return t.Length <= MaxTextLength ? t : throw new ValidationException(field, $"At most {MaxTextLength} characters.");
    }

    /// <summary>Dhaka calendar dates (yyyy-MM-dd, inclusive) to a UTC range [from 00:00 +06, day after "to" 00:00 +06).</summary>
    private static (DateTime? From, DateTime? To) Range(string? from, string? to)
    {
        static DateOnly? Day(string? text, string field)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            return DateOnly.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
                ? d : throw new ValidationException(field, "Use a date like 2026-11-21 (Dhaka calendar date).");
        }

        var f = Day(from, "from");
        var t = Day(to, "to");
        if (f is { } a && t is { } b && a > b) throw new ValidationException("from", "'from' must not be after 'to'.");
        return (f is { } x ? DateTime.SpecifyKind(x.ToDateTime(TimeOnly.MinValue) - Dhaka, DateTimeKind.Utc) : null,
            t is { } y ? DateTime.SpecifyKind(y.AddDays(1).ToDateTime(TimeOnly.MinValue) - Dhaka, DateTimeKind.Utc) : null);
    }
}
