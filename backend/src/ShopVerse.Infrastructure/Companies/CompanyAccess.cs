using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Companies;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Companies;

/// <summary>
/// The one place that decides what a user may do inside a company. Access comes from the user's active
/// <c>company_members</c> row, never from a token claim. Not a member → 404 (the company's existence is not
/// revealed); a member without the needed role → 403.
/// </summary>
internal static class CompanyAccess
{
    public static async Task<CompanyMember> RequireMemberAsync(
        ShopVerseDbContext db, Guid actorUserId, Guid companyId, CompanyRoles? role, CancellationToken ct)
    {
        var member = await db.CompanyMembers.AsNoTracking()
            .FirstOrDefaultAsync(m => m.CompanyId == companyId && m.UserId == actorUserId && m.IsActive, ct)
            ?? throw new KeyNotFoundException("Company not found.");
        if (role is { } needed && !member.Has(needed))
            throw new ForbiddenException("COMPANY_ROLE_REQUIRED", "Your company role does not allow this action.");
        return member;
    }

    /// <summary>A suspended company can be read but not changed.</summary>
    public static async Task RequireNotSuspendedAsync(ShopVerseDbContext db, Guid companyId, CancellationToken ct)
    {
        var status = await db.Companies.AsNoTracking().Where(c => c.Id == companyId).Select(c => (CompanyStatus?)c.Status).FirstOrDefaultAsync(ct)
                     ?? throw new KeyNotFoundException("Company not found.");
        if (status == CompanyStatus.Suspended)
            throw new ConflictException("COMPANY_SUSPENDED", "The company account is suspended.");
    }
}
