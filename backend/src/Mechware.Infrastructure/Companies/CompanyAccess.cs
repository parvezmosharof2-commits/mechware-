using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Companies;

/// <summary>
/// The one place that decides what a user may do inside a company. Access comes from the user's active
/// <c>company_members</c> row, never from a token claim. Not a member → 404 (the company's existence is not
/// revealed); a member without the needed role → 403.
/// </summary>
internal static class CompanyAccess
{
    /// <summary>
    /// Memberships that count: active and accepted. An invitation not yet accepted grants nothing (threat model v1, M2), so
    /// every query that asks "who is a member" goes through this one filter.
    /// </summary>
    public static IQueryable<CompanyMember> Effective(this IQueryable<CompanyMember> members) =>
        members.Where(m => m.IsActive && m.AcceptedAtUtc != null);

    public static async Task<CompanyMember> RequireMemberAsync(
        MechwareDbContext db, Guid actorUserId, Guid companyId, CompanyRoles? role, CancellationToken ct)
    {
        var member = await db.CompanyMembers.AsNoTracking().Effective()
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == actorUserId, ct)
            ?? throw new KeyNotFoundException("Company not found.");
        if (role is { } needed && !member.Has(needed))
            throw new ForbiddenException("COMPANY_ROLE_REQUIRED", "Your company role does not allow this action.");
        return member;
    }

    /// <summary>A suspended company can be read but not changed.</summary>
    public static async Task RequireNotSuspendedAsync(MechwareDbContext db, Guid companyId, CancellationToken ct)
    {
        var status = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => (CompanyStatus?)c.Status).FirstOrDefaultAsync(ct)
                     ?? throw new KeyNotFoundException("Company not found.");
        if (status == CompanyStatus.Suspended)
            throw new ConflictException("COMPANY_SUSPENDED", "The company account is suspended.");
        if (status == CompanyStatus.Rejected)
            throw new ConflictException("COMPANY_REJECTED", "MECHWARE did not accept this company's registration.");
    }
}
