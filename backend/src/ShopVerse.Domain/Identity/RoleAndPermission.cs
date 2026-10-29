using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Identity;

public sealed class Role : Entity<Guid>
{
    public string Name { get; private set; } = string.Empty;
    public string? Description { get; private set; }
    public bool IsSystem { get; private set; } = true;

    private Role() { }

    public Role(Guid id, string name, string? description, bool isSystem = true) : base(id)
    {
        Name = name.Trim().ToLowerInvariant();
        Description = description;
        IsSystem = isSystem;
    }
}

public sealed class Permission : Entity<Guid>
{
    public string Code { get; private set; } = string.Empty;
    public string Module { get; private set; } = string.Empty;
    public string? Description { get; private set; }

    private Permission() { }

    public Permission(Guid id, string code, string module, string? description = null) : base(id)
    {
        Code = code.Trim().ToLowerInvariant();
        Module = module.Trim().ToLowerInvariant();
        Description = description;
    }
}

public sealed class RolePermission
{
    public Guid RoleId { get; private set; }
    public Guid PermissionId { get; private set; }

    private RolePermission() { }

    public RolePermission(Guid roleId, Guid permissionId)
    {
        RoleId = roleId;
        PermissionId = permissionId;
    }
}

public sealed class UserRole
{
    public Guid UserId { get; private set; }
    public Guid RoleId { get; private set; }
    public string RoleName { get; private set; } = string.Empty;
    public DateTime AssignedAtUtc { get; private set; } = DateTime.UtcNow;

    private UserRole() { }

    public UserRole(Guid userId, Guid roleId, string roleName)
    {
        UserId = userId;
        RoleId = roleId;
        RoleName = roleName.Trim().ToLowerInvariant();
        AssignedAtUtc = DateTime.UtcNow;
    }
}
