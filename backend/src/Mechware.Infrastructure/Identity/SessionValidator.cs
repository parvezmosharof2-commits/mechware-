using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Identity;

/// <summary>
/// One query per authenticated request, so that suspending an account, taking away a role or a seller link, signing out or
/// resetting a password takes effect on the next request instead of when the 15-minute token expires.
/// </summary>
public sealed class SessionValidator(MechwareDbContext db) : ISessionValidator
{
    public async Task<LiveSession?> ValidateAsync(Guid userId, Guid sessionId, DateTime utcNow, CancellationToken cancellationToken = default)
    {
        var user = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId && u.DeletedAtUtc == null)
            .Select(u => new
            {
                u.Status,
                u.TwoFactorEnabled,
                u.SellerId,
                Roles = u.UserRoles.Select(r => r.RoleName).ToList(),
                // The session is open while its family still has a usable token. Revoking, or the family expiring, ends it.
                SessionOpen = db.RefreshTokens.Any(t => t.FamilyId == sessionId && t.UserId == u.Id
                    && !t.IsRevoked && !t.IsUsed && t.ExpiresAtUtc > utcNow),
            })
            .SingleOrDefaultAsync(cancellationToken);

        if (user is null || !user.SessionOpen || AccountRules.IsDisabled(user.Status))
        {
            return null;
        }

        IReadOnlyList<string> roles = AccountRules.EffectiveRoles(user.Roles);
        if (AccountRules.MustEnrollSecondFactor(user.TwoFactorEnabled, roles))
        {
            return null;
        }

        return new LiveSession(roles, AccountRules.PermissionsFor(roles), user.SellerId);
    }
}
