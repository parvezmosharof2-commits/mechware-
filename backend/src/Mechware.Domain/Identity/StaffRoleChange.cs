using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Identity;

/// <summary>The staff roles the console manages (G5.7). Buyer and seller roles are never set or removed here.</summary>
public static class StaffRoles
{
    public static readonly IReadOnlyList<string> All =
        [Permissions.Roles.Support, Permissions.Roles.Finance, Permissions.Roles.Moderator, Permissions.Roles.Admin];

    public static bool IsStaff(string role) => All.Contains(role.Trim().ToLowerInvariant());

    /// <summary>Fixed ids for <c>user_roles.role_id</c> (no foreign key; the seeded admin row uses ...0001).</summary>
    public static Guid IdOf(string role) => role switch
    {
        Permissions.Roles.Admin => Guid.Parse("00000000-0000-0000-0000-000000000001"),
        Permissions.Roles.Support => Guid.Parse("00000000-0000-0000-0000-000000000004"),
        Permissions.Roles.Finance => Guid.Parse("00000000-0000-0000-0000-000000000005"),
        Permissions.Roles.Moderator => Guid.Parse("00000000-0000-0000-0000-000000000006"),
        _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a staff role.")
    };

    /// <summary>The staff roles among <paramref name="roles"/>, lower case, sorted, once each.</summary>
    public static string[] Of(IEnumerable<string> roles) =>
        [.. roles.Select(r => r.Trim().ToLowerInvariant()).Where(All.Contains).Distinct().Order(StringComparer.Ordinal)];
}

public enum StaffRoleChangeStatus { Requested = 1, Approved = 2, Rejected = 3 }

/// <summary>
/// A change of one person's staff roles (G5.7): requested by one administrator (never for themselves), approved by ANOTHER who is
/// neither the requester nor the person changed (four eyes, domain + database CHECKs). Only the approval changes <c>user_roles</c>;
/// a rejection or a withdrawal changes nothing. One open request per person (partial unique index).
/// </summary>
public sealed class StaffRoleChange : Entity<Guid>
{
    public const int MinReasonLength = 5, MaxReasonLength = 500;

    public Guid UserId { get; private set; }
    public string[] RolesBefore { get; private set; } = [];
    public string[] RolesAfter { get; private set; } = [];
    public string Reason { get; private set; } = string.Empty;
    public StaffRoleChangeStatus Status { get; private set; } = StaffRoleChangeStatus.Requested;
    public Guid RequestedBy { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecisionReason { get; private set; }

    private StaffRoleChange() { }

    private StaffRoleChange(Guid id) : base(id) { }

    public bool RemovesAdmin => RolesBefore.Contains(Permissions.Roles.Admin) && !RolesAfter.Contains(Permissions.Roles.Admin);

    public static StaffRoleChange Request(Guid userId, IEnumerable<string> currentRoles, IEnumerable<string>? requestedRoles, string? reason,
        Guid requestedBy, DateTime nowUtc)
    {
        // Nobody changes their own roles, up or down: a raise always needs two other people (the requester and the approver).
        if (requestedBy == userId)
            throw new ForbiddenException("ROLE_CHANGE_SELF", "Another administrator must ask for a change of your roles.");
        var asked = (requestedRoles ?? []).Select(r => (r ?? string.Empty).Trim().ToLowerInvariant()).ToList();
        if (asked.Any(r => !StaffRoles.IsStaff(r)))
            throw new ValidationException("roles", $"Use only staff roles: {string.Join(", ", StaffRoles.All)}.");
        var before = StaffRoles.Of(currentRoles);
        var after = StaffRoles.Of(asked);
        if (before.SequenceEqual(after)) throw new ValidationException("roles", "These are already the person's staff roles.");
        var why = (reason ?? string.Empty).Trim();
        if (why.Length is < MinReasonLength or > MaxReasonLength)
            throw new ValidationException("reason", $"Give a reason of {MinReasonLength} to {MaxReasonLength} characters.");

        return new StaffRoleChange(Guid.NewGuid())
        {
            UserId = userId,
            RolesBefore = before,
            RolesAfter = after,
            Reason = why,
            RequestedBy = requestedBy,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    /// <summary>Four eyes: neither the requester nor the person whose roles change may approve.</summary>
    public void Approve(Guid approverId, DateTime nowUtc)
    {
        Expect();
        if (approverId == UserId) throw new ForbiddenException("ROLE_CHANGE_SELF", "Nobody approves a change of their own roles.");
        if (approverId == RequestedBy)
            throw new ConflictException("ROLE_CHANGE_FOUR_EYES", "Another administrator must approve the change you asked for.");
        (Status, DecidedBy, DecidedAtUtc, UpdatedAtUtc) = (StaffRoleChangeStatus.Approved, approverId, nowUtc, nowUtc);
    }

    /// <summary>Anyone allowed to decide, the requester included (a withdrawal), but not the person changed.</summary>
    public void Reject(Guid userId, string? reason, DateTime nowUtc)
    {
        Expect();
        if (userId == UserId) throw new ForbiddenException("ROLE_CHANGE_SELF", "Nobody decides a change of their own roles.");
        var why = (reason ?? string.Empty).Trim();
        if (why.Length is < MinReasonLength or > MaxReasonLength)
            throw new ValidationException("reason", $"Give a reason of {MinReasonLength} to {MaxReasonLength} characters.");
        (Status, DecidedBy, DecidedAtUtc, DecisionReason, UpdatedAtUtc) = (StaffRoleChangeStatus.Rejected, userId, nowUtc, why, nowUtc);
    }

    private void Expect()
    {
        if (Status != StaffRoleChangeStatus.Requested)
            throw new ConflictException("ROLE_CHANGE_NOT_REQUESTED", $"A {Status.ToString().ToLowerInvariant()} role change cannot be decided again.");
    }
}
