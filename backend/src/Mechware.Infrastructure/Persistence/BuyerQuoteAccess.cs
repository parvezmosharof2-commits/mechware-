using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;

namespace Mechware.Infrastructure.Persistence;

/// <summary>What the database says about one person and one company right now.</summary>
public sealed record BuyerQuoteMember(CompanyRoles Roles, CompanyStatus CompanyStatus)
{
    /// <summary>Reads are open to Active and Suspended companies; Pending and Rejected ones look like they do not exist (ADR-0011 §13).</summary>
    public bool MayRead => CompanyStatus is CompanyStatus.Active or CompanyStatus.Suspended;
}

/// <summary>
/// The ONE rule for "is this person an effective member of this company" on the buyer RFQ and quote routes (ADR-0011 §13; the
/// B3.9 pattern): active, invitation accepted, read from <c>company_members</c> on every call for the company in the URL, never for
/// "any company the person belongs to". Null means not a member (or no such company): the caller answers one 404.
/// </summary>
public static class BuyerQuoteAccess
{
    public static Task<BuyerQuoteMember?> FindAsync(MechwareDbContext db, Guid userId, Guid companyId, CancellationToken ct) =>
        (from m in db.CompanyMembers.AsNoTracking()
         join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id
         where m.CompanyId == companyId && m.UserId == userId && m.IsActive && m.AcceptedAtUtc != null
         select new BuyerQuoteMember(m.Roles, c.Status)).FirstOrDefaultAsync(ct);

    /// <summary>
    /// Every company a (staff) user is an effective member of, in any status: the conflict-of-interest input of the quote desk
    /// (ADR-0011 §5, QT6), read in the same request, never from the body.
    /// </summary>
    public static async Task<IReadOnlyCollection<Guid>> CompanyIdsAsync(MechwareDbContext db, Guid userId, CancellationToken ct) =>
        await db.CompanyMembers.AsNoTracking().Where(m => m.UserId == userId && m.IsActive && m.AcceptedAtUtc != null)
            .Select(m => m.CompanyId).Distinct().ToListAsync(ct);
}
