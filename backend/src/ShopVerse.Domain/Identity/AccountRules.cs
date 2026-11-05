namespace ShopVerse.Domain.Identity;

/// <summary>
/// The account rules every sign-in path and every authenticated request share, kept in one place so login, refresh and the
/// per-request session check cannot drift apart.
/// </summary>
public static class AccountRules
{
    /// <summary>A disabled account starts no session and keeps none.</summary>
    public static bool IsDisabled(UserStatus status) =>
        status is UserStatus.Suspended or UserStatus.Banned or UserStatus.Deleted;

    /// <summary>The roles a user holds; an account with none is a buyer.</summary>
    public static IReadOnlyList<string> EffectiveRoles(IEnumerable<string> roleNames)
    {
        List<string> roles = roleNames.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return roles.Count > 0 ? roles.AsReadOnly() : [Permissions.Roles.Buyer];
    }

    public static IReadOnlyList<string> PermissionsFor(IReadOnlyList<string> roles) =>
        roles.SelectMany(Permissions.GetDefaultPermissionsForRole).Distinct(StringComparer.OrdinalIgnoreCase).ToList().AsReadOnly();

    /// <summary>Password and authenticator: the user turned 2FA on, or one of the roles demands it.</summary>
    public static bool RequiresSecondFactor(bool twoFactorEnabled, IEnumerable<string> roles) =>
        twoFactorEnabled || roles.Any(Permissions.Roles.RequiresMandatoryTwoFactor);

    /// <summary>A role demands an authenticator this person has not enrolled: no session until they do (M1).</summary>
    public static bool MustEnrollSecondFactor(bool twoFactorEnabled, IEnumerable<string> roles) =>
        !twoFactorEnabled && roles.Any(Permissions.Roles.RequiresMandatoryTwoFactor);
}
