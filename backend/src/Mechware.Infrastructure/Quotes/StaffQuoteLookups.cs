using Microsoft.EntityFrameworkCore;
using Mechware.Application.Quotes;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Quotes;

/// <summary><see cref="IStaffQuoteLookups"/> (D4.18): field-by-field answers, nothing secret and no cost.</summary>
public sealed class StaffQuoteLookups(MechwareDbContext db) : IStaffQuoteLookups
{
    private async Task EnsureCompanyAsync(Guid companyId, CancellationToken ct)
    {
        if (!await db.Companies.AsNoTracking().AnyAsync(c => c.Id == companyId, ct)) throw new KeyNotFoundException("Company not found.");
    }

    public async Task<IReadOnlyList<StaffSiteDto>> SitesAsync(Guid companyId, CancellationToken ct = default)
    {
        await EnsureCompanyAsync(companyId, ct);
        return await db.CompanySites.AsNoTracking().Where(s => s.CompanyId == companyId).OrderByDescending(s => s.IsDefaultDelivery).ThenBy(s => s.Name).ThenBy(s => s.Id)
            .Select(s => new StaffSiteDto(s.Id, s.Name, s.Line1, s.Line2, s.Upazila, s.District, s.Division, s.PostalCode, s.IsDefaultDelivery)).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<StaffContactDto>> ContactsAsync(Guid companyId, CancellationToken ct = default)
    {
        await EnsureCompanyAsync(companyId, ct);
        // The same rule as ContactAsync of the desk: active, accepted, Buyer or Admin (checked in memory: a few members per company).
        var members = await (from m in db.CompanyMembers.AsNoTracking()
                             join u in db.Users.AsNoTracking() on m.UserId equals u.Id
                             where m.CompanyId == companyId && m.IsActive && m.AcceptedAtUtc != null && u.Status == UserStatus.Active && u.DeletedAtUtc == null
                             select new { m.UserId, m.Roles, u.FullName, u.Email }).ToListAsync(ct);
        return [.. members.Where(m => (m.Roles & (CompanyRoles.Buyer | CompanyRoles.Admin)) != 0).OrderBy(m => m.FullName).ThenBy(m => m.UserId)
            .Select(m => new StaffContactDto(m.UserId, m.FullName, m.Email,
                [.. new[] { CompanyRoles.Admin, CompanyRoles.Buyer, CompanyRoles.Approver, CompanyRoles.Requester }.Where(r => (m.Roles & r) != 0).Select(r => r.ToString().ToLowerInvariant())]))];
    }

    public async Task<IReadOnlyList<StaffVariantSellerDto>> SellersOfAsync(IReadOnlyList<Guid> variantIds, CancellationToken ct = default)
    {
        var ids = variantIds.Distinct().ToList();
        if (ids.Count is 0 or > IStaffQuoteLookups.MaxVariants)
            throw new ValidationException("variantIds", $"Give between 1 and {IStaffQuoteLookups.MaxVariants} variant ids.");
        return await (from v in db.ProductVariants.AsNoTracking()
                      join p in db.Products.AsNoTracking() on v.ProductId equals p.Id
                      join s in db.Sellers.AsNoTracking() on p.SellerId equals s.Id
                      where ids.Contains(v.Id) && p.DeletedAtUtc == null
                      orderby v.Sku, v.Id
                      select new StaffVariantSellerDto(v.Id, s.Id, s.StoreName)).ToListAsync(ct);
    }
}
