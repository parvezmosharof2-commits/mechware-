using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Identity;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff users and roles for the console (G5.7, <c>users.manage</c>: Admin only). Reads never carry a hash, secret, code or token.
/// Disable and enable need an authenticator code; disabling ends every session of the person at once. A role change is four eyes:
/// one administrator asks (<c>PUT .../roles</c>, step-up), ANOTHER approves with a code; nobody asks for or decides their own roles,
/// and no change leaves the platform without an active administrator. Only staff roles (support, finance, moderator, admin).
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminStaffUsersController(IStaffUserService staff) : ControllerBase
{
    public const string DisableStepUpAction = "user.disable";
    public const string EnableStepUpAction = "user.enable";
    public const string RoleChangeRequestStepUpAction = "user.roles.request";
    public const string RoleChangeApproveStepUpAction = "user.roles.approve";

    /// <summary>Staff accounts by e-mail; <c>q</c> (part of e-mail or name; a full e-mail also finds a non-staff account), <c>role</c>, <c>status</c>, <c>page</c>, <c>pageSize</c> (max 50).</summary>
    [HttpGet("users")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> List([FromQuery] string? q, [FromQuery] string? role, [FromQuery] string? status,
        [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        var result = await staff.ListAsync(new StaffUserFilter(q, role, status, page, pageSize), ct);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = result });
    }

    [HttpGet("users/{userId:guid}")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> Get(Guid userId, CancellationToken ct)
    {
        var result = await staff.GetAsync(userId, ct);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = result });
    }

    [HttpPost("users/{userId:guid}/disable")]
    [RequirePermission(Permissions.UsersManage)]
    [RequireStepUp(DisableStepUpAction)]
    public async Task<IActionResult> Disable(Guid userId, [FromBody] StaffUserReasonRequest body, CancellationToken ct) =>
        Ok(new { data = await staff.DisableAsync(userId, body, ct) });

    [HttpPost("users/{userId:guid}/enable")]
    [RequirePermission(Permissions.UsersManage)]
    [RequireStepUp(EnableStepUpAction)]
    public async Task<IActionResult> Enable(Guid userId, [FromBody] StaffUserReasonRequest? body, CancellationToken ct) =>
        Ok(new { data = await staff.EnableAsync(userId, body ?? new StaffUserReasonRequest(null), ct) });

    /// <summary>Asks for the person's full set of staff roles to become <c>roles</c> (buyer/seller roles stay); 202, nothing changes until approved.</summary>
    [HttpPut("users/{userId:guid}/roles")]
    [RequirePermission(Permissions.UsersManage)]
    [RequireStepUp(RoleChangeRequestStepUpAction)]
    public async Task<IActionResult> RequestRoles(Guid userId, [FromBody] StaffRoleChangeRequest body, CancellationToken ct) =>
        Accepted("/api/v1/admin/role-changes?status=requested", new { data = await staff.RequestRoleChangeAsync(userId, body, ct) });

    [HttpGet("role-changes")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> ListRoleChanges([FromQuery] string? status, CancellationToken ct)
    {
        var result = await staff.ListRoleChangesAsync(status, ct);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = result });
    }

    [HttpPost("role-changes/{changeId:guid}/approve")]
    [RequirePermission(Permissions.UsersManage)]
    [RequireStepUp(RoleChangeApproveStepUpAction)]
    public async Task<IActionResult> ApproveRoles(Guid changeId, CancellationToken ct) =>
        Ok(new { data = await staff.ApproveRoleChangeAsync(changeId, ct) });

    /// <summary>Rejects, or (by the requester) withdraws; nothing changes, so no code.</summary>
    [HttpPost("role-changes/{changeId:guid}/reject")]
    [RequirePermission(Permissions.UsersManage)]
    public async Task<IActionResult> RejectRoles(Guid changeId, [FromBody] StaffUserReasonRequest body, CancellationToken ct) =>
        Ok(new { data = await staff.RejectRoleChangeAsync(changeId, body, ct) });

    /// <summary>The staff roles and their permissions, from the code (read-only: a role's permissions change by deployment).</summary>
    [HttpGet("roles")]
    [RequirePermission(Permissions.UsersManage)]
    public IActionResult Roles() => Ok(new { data = staff.Roles() });
}
