namespace Mechware.Application.Identity;

/// <summary>
/// Filters of the staff user list (G5.7). <c>Q</c> matches part of the e-mail or name of staff accounts; a full e-mail address also
/// finds an account without a staff role (to give it one). <c>Role</c> and <c>Status</c> are exact (wire names).
/// </summary>
public sealed record StaffUserFilter(string? Q, string? Role, string? Status, int Page, int PageSize);

/// <summary>One account as the console sees it: never a hash, a secret, a code or a token.</summary>
public sealed record StaffUserDto(
    Guid Id, string Email, string FullName, IReadOnlyList<string> Roles, string Status, bool TwoFactorEnrolled, DateTime? LastSignInAtUtc,
    DateTime CreatedAtUtc);

public sealed record StaffUserDetailDto(StaffUserDto User, StaffRoleChangeDto? PendingRoleChange);

public sealed record StaffUserPageDto(IReadOnlyList<StaffUserDto> Items, int Page, int PageSize, int TotalCount);

public sealed record StaffRoleDto(string Name, IReadOnlyList<string> Permissions);

public sealed record StaffRoleChangeDto(
    Guid Id, Guid UserId, IReadOnlyList<string> RolesBefore, IReadOnlyList<string> RolesAfter, string Reason, string Status, Guid RequestedBy,
    DateTime RequestedAtUtc, Guid? DecidedBy, DateTime? DecidedAtUtc, string? DecisionReason);

public sealed record StaffUserReasonRequest(string? Reason);

public sealed record StaffRoleChangeRequest(IReadOnlyList<string>? Roles, string? Reason);

/// <summary>
/// Staff users and roles (G5.7, users.manage). Disable/enable and role changes are step-up at the controller; role changes are four
/// eyes. No change ever leaves the platform without an active administrator; disabling ends the person's sessions at once.
/// </summary>
public interface IStaffUserService
{
    const int MaxPageSize = 50;

    Task<StaffUserPageDto> ListAsync(StaffUserFilter filter, CancellationToken ct = default);

    Task<StaffUserDetailDto> GetAsync(Guid userId, CancellationToken ct = default);

    IReadOnlyList<StaffRoleDto> Roles();

    Task<StaffUserDto> DisableAsync(Guid userId, StaffUserReasonRequest request, CancellationToken ct = default);

    Task<StaffUserDto> EnableAsync(Guid userId, StaffUserReasonRequest request, CancellationToken ct = default);

    Task<StaffRoleChangeDto> RequestRoleChangeAsync(Guid userId, StaffRoleChangeRequest request, CancellationToken ct = default);

    Task<IReadOnlyList<StaffRoleChangeDto>> ListRoleChangesAsync(string? status, CancellationToken ct = default);

    Task<StaffRoleChangeDto> ApproveRoleChangeAsync(Guid changeId, CancellationToken ct = default);

    Task<StaffRoleChangeDto> RejectRoleChangeAsync(Guid changeId, StaffUserReasonRequest request, CancellationToken ct = default);
}
