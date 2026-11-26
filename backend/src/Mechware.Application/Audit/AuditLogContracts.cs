using System.Text.Json.Nodes;
using Mechware.Application.Catalog;

namespace Mechware.Application.Audit;

/// <summary>
/// Filters of the audit list (G5.2b). Every member is optional; text filters are exact matches (trimmed, at most 100 characters).
/// <c>From</c>/<c>To</c> are Dhaka calendar dates (yyyy-MM-dd, inclusive).
/// </summary>
public sealed record AuditLogFilter(
    Guid? ActorId, string? Action, string? EntityName, string? EntityId, string? CorrelationId, string? From, string? To, int Page, int PageSize);

/// <summary>
/// One audit row without its values. <paramref name="ActorEmail"/> is the actor account's e-mail today (null for the system or a removed
/// account); <paramref name="ChainPosition"/> is null until the row is sealed into the hash chain.
/// </summary>
public sealed record AuditLogListItemDto(
    Guid Id, DateTime CreatedAtUtc, Guid? ActorId, string? ActorEmail, string? ActorIp, string Action, string EntityName, string EntityId,
    string? CorrelationId, long? ChainPosition);

/// <summary>
/// One audit row with its before/after values after server-side redaction: secrets, codes, hashes, tokens, encrypted business numbers
/// and bank references read <c>"[hidden]"</c>; an encrypted value is never decrypted. Null when the row has no values on that side.
/// </summary>
public sealed record AuditLogDetailDto(
    Guid Id, DateTime CreatedAtUtc, Guid? ActorId, string? ActorEmail, string? ActorIp, string Action, string EntityName, string EntityId,
    string? CorrelationId, long? ChainPosition, JsonNode? OldValues, JsonNode? NewValues);

/// <summary>Staff reads of the audit log (audit.view). Each read is itself written to the audit log.</summary>
public interface IAuditLogReadService
{
    const int MaxPageSize = 50;

    /// <summary>The action written when someone lists the audit log.</summary>
    const string SearchedAction = "audit.searched";

    /// <summary>The action written when someone opens one audit row.</summary>
    const string ViewedAction = "audit.viewed";

    /// <summary>Newest first; <c>pageSize</c> is clamped to 1..<see cref="MaxPageSize"/>. A bad filter is a validation error.</summary>
    Task<PagedResult<AuditLogListItemDto>> ListAsync(AuditLogFilter filter, CancellationToken ct = default);

    /// <summary>One row, redacted; an unknown id throws <see cref="KeyNotFoundException"/>.</summary>
    Task<AuditLogDetailDto> GetAsync(Guid id, CancellationToken ct = default);
}
