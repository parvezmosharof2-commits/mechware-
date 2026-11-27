using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Configurations;

namespace Mechware.Infrastructure.Identity;

/// <summary>
/// G5.7 staff users and roles. Every write that can take an administrator away (disable, a role change at request and approval) runs
/// under one transaction-level advisory lock and counts the OTHER active administrators with an authenticator: two admins
/// disabling or demoting each other at the same moment are serialised, and the second finds nobody left and is refused.
/// Disabling revokes every refresh token of the person in the same transaction; the per-request session check refuses the
/// access token at once.
/// </summary>
public sealed class StaffUserService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock) : IStaffUserService
{
    /// <summary>The advisory lock key of every change that can remove an administrator.</summary>
    private const long AdminLockKey = 0x4D57_4735_3741;   // "MWG57A"

    private const int MaxQueryLength = 100;
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly string[] Staff = [.. StaffRoles.All];

    private static readonly Dictionary<string, UserStatus> Statuses = new(StringComparer.Ordinal)
    {
        [Wire(UserStatus.Active)] = UserStatus.Active,
        [Wire(UserStatus.PendingVerification)] = UserStatus.PendingVerification,
        [Wire(UserStatus.Suspended)] = UserStatus.Suspended,
        [Wire(UserStatus.Banned)] = UserStatus.Banned,
    };

    public async Task<StaffUserPageDto> ListAsync(StaffUserFilter f, CancellationToken ct = default)
    {
        var q = f.Q?.Trim();
        if (q is { Length: > MaxQueryLength }) throw new ValidationException("q", $"Search with at most {MaxQueryLength} characters.");
        var role = f.Role?.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(role) && !StaffRoles.IsStaff(role))
            throw new ValidationException("role", $"Use one of: {string.Join(", ", StaffRoles.All)}.");
        UserStatus? status = null;
        if (!string.IsNullOrWhiteSpace(f.Status))
            status = Statuses.TryGetValue(f.Status.Trim().ToLowerInvariant(), out var s)
                ? s : throw new ValidationException("status", $"Use one of: {string.Join(", ", Statuses.Keys)}.");
        var (page, pageSize) = (Math.Max(1, f.Page), Math.Clamp(f.PageSize, 1, IStaffUserService.MaxPageSize));

        // Staff accounts only; a full e-mail address also finds a buyer account, so an admin can ask to give it a staff role.
        var like = string.IsNullOrEmpty(q) ? null : "%" + q.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
        var exact = q is not null && q.Contains('@') ? q.ToLowerInvariant() : null;
        var users = db.Users.AsNoTracking().Where(u => u.DeletedAtUtc == null && u.Status != UserStatus.Deleted)
            .Where(u => (u.UserRoles.Any(r => Staff.Contains(r.RoleName))
                         && (like == null || EF.Functions.ILike(u.Email, like, "\\") || EF.Functions.ILike(u.FullName, like, "\\")))
                        || (exact != null && u.Email == exact));
        if (!string.IsNullOrEmpty(role)) users = users.Where(u => u.UserRoles.Any(r => r.RoleName == role));
        if (status is { } st) users = users.Where(u => u.Status == st);

        var total = await users.CountAsync(ct);
        var rows = await Project(users.OrderBy(u => u.Email).Skip((page - 1) * pageSize).Take(pageSize)).ToListAsync(ct);
        return new StaffUserPageDto([.. rows.Select(ToDto)], page, pageSize, total);
    }

    public async Task<StaffUserDetailDto> GetAsync(Guid userId, CancellationToken ct = default)
    {
        var row = await Project(db.Users.AsNoTracking().Where(u => u.Id == userId && u.DeletedAtUtc == null && u.Status != UserStatus.Deleted))
            .SingleOrDefaultAsync(ct) ?? throw new KeyNotFoundException("User not found.");
        var pending = await db.StaffRoleChanges.AsNoTracking()
            .SingleOrDefaultAsync(c => c.UserId == userId && c.Status == StaffRoleChangeStatus.Requested, ct);
        return new StaffUserDetailDto(ToDto(row), pending is null ? null : ToDto(pending));
    }

    public IReadOnlyList<StaffRoleDto> Roles() =>
        [.. StaffRoles.All.Select(r => new StaffRoleDto(r, [.. Permissions.GetDefaultPermissionsForRole(r).Order(StringComparer.Ordinal)]))];

    public async Task<StaffUserDto> DisableAsync(Guid userId, StaffUserReasonRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        var reason = Reason(request.Reason, required: true);
        if (actor == userId) throw new ConflictException("USER_DISABLE_SELF", "You cannot disable your own account.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAdminsAsync(ct);
        var target = await StaffAccountAsync(userId, ct);
        var roles = StaffRoles.Of(target.UserRoles.Select(r => r.RoleName));
        if (roles.Contains(Permissions.Roles.Admin)) await EnsureAnotherAdminAsync(userId, ct);
        target.Suspend(now);
        // Every session ends now: the refresh tokens are revoked, so even a later enable cannot bring an old session back.
        var revoked = await db.RefreshTokens.Where(t => t.UserId == userId && !t.IsRevoked)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.IsRevoked, true).SetProperty(t => t.UpdatedAtUtc, (DateTime?)now), ct);
        db.AuditLogs.Add(Audit("user.disabled", "users", userId, new { Status = Wire(UserStatus.Active) },
            new { Status = Wire(UserStatus.Suspended), Roles = roles, RevokedSessions = revoked, Reason = reason }, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return await DtoAsync(userId, ct);
    }

    public async Task<StaffUserDto> EnableAsync(Guid userId, StaffUserReasonRequest request, CancellationToken ct = default)
    {
        var now = Now;
        var reason = Reason(request.Reason, required: false);
        var target = await StaffAccountAsync(userId, ct);
        target.Reactivate(now);
        db.AuditLogs.Add(Audit("user.enabled", "users", userId, new { Status = Wire(UserStatus.Suspended) },
            new { Status = Wire(UserStatus.Active), Reason = reason }, now));
        await db.SaveChangesAsync(ct);
        return await DtoAsync(userId, ct);
    }

    public async Task<StaffRoleChangeDto> RequestRoleChangeAsync(Guid userId, StaffRoleChangeRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAdminsAsync(ct);
        var target = await db.Users.AsNoTracking().Include(u => u.UserRoles)
            .SingleOrDefaultAsync(u => u.Id == userId && u.DeletedAtUtc == null && u.Status != UserStatus.Deleted, ct)
            ?? throw new KeyNotFoundException("User not found.");
        var change = StaffRoleChange.Request(userId, target.UserRoles.Select(r => r.RoleName), request.Roles, request.Reason, actor, now);
        if (await db.StaffRoleChanges.AnyAsync(c => c.UserId == userId && c.Status == StaffRoleChangeStatus.Requested, ct)) throw Pending();
        if (change.RemovesAdmin) await EnsureAnotherAdminAsync(userId, ct);   // an early answer; the approval checks again

        db.StaffRoleChanges.Add(change);
        db.AuditLogs.Add(Audit("user.role_change_requested", "staff_role_changes", change.Id, null,
            new { change.UserId, change.RolesBefore, change.RolesAfter, change.Reason }, now));
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            throw Pending();
        }
        await tx.CommitAsync(ct);
        return ToDto(change);
    }

    public async Task<IReadOnlyList<StaffRoleChangeDto>> ListRoleChangesAsync(string? status, CancellationToken ct = default)
    {
        var wanted = string.IsNullOrWhiteSpace(status) ? StaffRoleChangeStatus.Requested
            : Enum.GetValues<StaffRoleChangeStatus>().FirstOrDefault(s => Wire(s) == status.Trim().ToLowerInvariant());
        if (wanted == 0) throw new ValidationException("status", "Use requested, approved or rejected.");
        var rows = await db.StaffRoleChanges.AsNoTracking().Where(c => c.Status == wanted)
            .OrderByDescending(c => c.CreatedAtUtc).Take(100).ToListAsync(ct);
        return [.. rows.Select(ToDto)];
    }

    public async Task<StaffRoleChangeDto> ApproveRoleChangeAsync(Guid changeId, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        await LockAdminsAsync(ct);
        var change = await LockChangeAsync(changeId, ct);
        change.Approve(actor, now);   // four eyes: not the requester, not the person changed
        var target = await db.Users.Include(u => u.UserRoles).SingleOrDefaultAsync(u => u.Id == change.UserId && u.DeletedAtUtc == null, ct)
            ?? throw new KeyNotFoundException("User not found.");
        var current = StaffRoles.Of(target.UserRoles.Select(r => r.RoleName));
        if (!current.SequenceEqual(change.RolesBefore))
            throw new ConflictException("ROLE_CHANGE_STALE", "The person's roles changed after this request; withdraw it and ask again.");
        if (change.RemovesAdmin) await EnsureAnotherAdminAsync(target.Id, ct);

        db.UserRoles.RemoveRange(target.UserRoles.Where(r => StaffRoles.IsStaff(r.RoleName) && !change.RolesAfter.Contains(r.RoleName)).ToList());
        foreach (var role in change.RolesAfter.Except(current)) db.UserRoles.Add(new UserRole(target.Id, StaffRoles.IdOf(role), role));
        db.AuditLogs.Add(Audit("user.roles_changed", "users", target.Id, new { Roles = change.RolesBefore },
            new { Roles = change.RolesAfter, ChangeId = change.Id, change.RequestedBy, change.Reason }, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(change);
    }

    public async Task<StaffRoleChangeDto> RejectRoleChangeAsync(Guid changeId, StaffUserReasonRequest request, CancellationToken ct = default)
    {
        var (actor, now) = (Actor, Now);
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var change = await LockChangeAsync(changeId, ct);
        change.Reject(actor, request.Reason, now);   // the requester may withdraw; nothing changes
        db.AuditLogs.Add(Audit("user.role_change_rejected", "staff_role_changes", change.Id, null,
            new { change.UserId, Withdrawn = actor == change.RequestedBy, Reason = change.DecisionReason }, now));
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(change);
    }

    /// <summary>
    /// Refuses when no OTHER account would remain an active administrator with an authenticator (one that can actually sign in).
    /// Call only under <see cref="LockAdminsAsync"/>.
    /// </summary>
    private async Task EnsureAnotherAdminAsync(Guid leaving, CancellationToken ct)
    {
        var others = await db.Users.CountAsync(u => u.Id != leaving && u.DeletedAtUtc == null && u.Status == UserStatus.Active
            && u.TwoFactorEnabled && u.UserRoles.Any(r => r.RoleName == Permissions.Roles.Admin), ct);
        if (others == 0) throw new ConflictException("LAST_ADMIN", "This is the last active administrator; make someone else an administrator first.");
    }

    private Task LockAdminsAsync(CancellationToken ct) =>
        db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock({AdminLockKey})", ct);

    private async Task<StaffRoleChange> LockChangeAsync(Guid id, CancellationToken ct) =>
        (await db.StaffRoleChanges.FromSqlInterpolated($"SELECT * FROM staff_role_changes WHERE id = {id} FOR UPDATE").ToListAsync(ct))
        .SingleOrDefault() ?? throw new KeyNotFoundException("Role change not found.");

    /// <summary>A staff account (tracked, with roles); buyer and seller accounts are not disabled or enabled here.</summary>
    private async Task<User> StaffAccountAsync(Guid userId, CancellationToken ct)
    {
        var target = await db.Users.Include(u => u.UserRoles)
            .SingleOrDefaultAsync(u => u.Id == userId && u.DeletedAtUtc == null && u.Status != UserStatus.Deleted, ct)
            ?? throw new KeyNotFoundException("User not found.");
        if (StaffRoles.Of(target.UserRoles.Select(r => r.RoleName)).Length == 0)
            throw new ConflictException("NOT_STAFF_ACCOUNT", "Only staff accounts are disabled or enabled here.");
        return target;
    }

    private static string? Reason(string? reason, bool required)
    {
        var why = reason?.Trim();
        if (string.IsNullOrEmpty(why) && !required) return null;
        if (why is not { Length: >= StaffRoleChange.MinReasonLength and <= StaffRoleChange.MaxReasonLength })
            throw new ValidationException("reason", $"Give a reason of {StaffRoleChange.MinReasonLength} to {StaffRoleChange.MaxReasonLength} characters.");
        return why;
    }

    private static ConflictException Pending() =>
        new("ROLE_CHANGE_PENDING", "A role change for this person is already waiting; decide or withdraw it first.");

    private sealed record Row(Guid Id, string Email, string FullName, List<string> Roles, UserStatus Status, bool TwoFactorEnabled,
        DateTime? LastLoginAtUtc, DateTime CreatedAtUtc);

    /// <summary>Only these columns leave the database: no hash, secret, recovery code or token is ever read.</summary>
    private static IQueryable<Row> Project(IQueryable<User> users) => users.Select(u => new Row(u.Id, u.Email, u.FullName,
        u.UserRoles.Select(r => r.RoleName).ToList(), u.Status, u.TwoFactorEnabled, u.LastLoginAtUtc, u.CreatedAtUtc));

    private async Task<StaffUserDto> DtoAsync(Guid userId, CancellationToken ct) =>
        ToDto(await Project(db.Users.AsNoTracking().Where(u => u.Id == userId)).SingleAsync(ct));

    private static StaffUserDto ToDto(Row r) => new(r.Id, r.Email, r.FullName,
        [.. AccountRules.EffectiveRoles(r.Roles).Select(x => x.ToLowerInvariant()).Order(StringComparer.Ordinal)], Wire(r.Status),
        r.TwoFactorEnabled, r.LastLoginAtUtc, r.CreatedAtUtc);

    private static StaffRoleChangeDto ToDto(StaffRoleChange c) => new(c.Id, c.UserId, c.RolesBefore, c.RolesAfter, c.Reason, Wire(c.Status),
        c.RequestedBy, c.CreatedAtUtc, c.DecidedBy, c.DecidedAtUtc, c.DecisionReason);

    private Guid Actor => user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private DateTime Now => clock.GetUtcNow().UtcDateTime;

    private AuditLog Audit(string action, string entity, Guid id, object? before, object after, DateTime now) =>
        new(Guid.NewGuid(), Actor, user.IpAddress, action, entity, id.ToString(),
            before is null ? null : JsonSerializer.Serialize(before, Json), JsonSerializer.Serialize(after, Json), user.CorrelationId, now);

    private static string Wire<T>(T value) where T : struct, Enum => SnakeCaseEnumConverter<T>.ToSnake(value.ToString());
}
