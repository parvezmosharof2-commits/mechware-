using Microsoft.EntityFrameworkCore;
using Mechware.Application.Credit;
using Mechware.Domain.Companies;

namespace Mechware.Infrastructure.Persistence;

/// <summary>
/// The ONE rule for "may this person see this company's credit" (ADR-0010 §10, OWNER 6, threat model CS22/CS24), shared by the buyer
/// credit reads (Credit) and <c>OrderDto.credit</c> (Orders): an effective member (active, invitation accepted) holding a role in
/// <see cref="CreditOptions.BalanceVisibleTo"/> of a company that is Active or Suspended. Read from the database on every call.
/// </summary>
public static class BuyerCreditAccess
{
    public static async Task<bool> MayViewAsync(MechwareDbContext db, Guid actorUserId, Guid companyId, CreditOptions options, CancellationToken ct)
    {
        var roles = await db.CompanyMembers.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.UserId == actorUserId && m.IsActive && m.AcceptedAtUtc != null)
            .Select(m => (CompanyRoles?)m.Roles).FirstOrDefaultAsync(ct);
        return roles is { } held && (held & options.BalanceVisibleTo) != 0
            && await db.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId && (c.Status == CompanyStatus.Active || c.Status == CompanyStatus.Suspended), ct);
    }
}
