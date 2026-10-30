using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Companies;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Companies;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Companies;

public sealed class CompanyService(ShopVerseDbContext db) : ICompanyService
{
    private const int MaxPageSize = 100;

    // ---------- registration & reads ----------

    public async Task<CompanyDto> RegisterAsync(Guid actorUserId, RegisterCompanyRequest request, CancellationToken ct = default)
    {
        var company = Company.Register(Guid.NewGuid(), request.LegalName, request.TradeLicenceNo, request.Bin, request.Tin, request.Industry);

        if (await db.Companies.AnyAsync(c => c.Bin == company.Bin, ct))
            throw new ConflictException("COMPANY_BIN_EXISTS", "A company with this BIN is already registered.");

        db.Companies.Add(company);
        // The registrant becomes the first Company Admin; the company stays Pending until MECHWARE verifies it.
        db.CompanyMembers.Add(new CompanyMember(Guid.NewGuid(), company.Id, actorUserId, CompanyRoles.Admin, null, null));

        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new ConflictException("COMPANY_BIN_EXISTS", "A company with this BIN is already registered.");
        }

        return ToDto(company);
    }

    public async Task<IReadOnlyList<MyCompanyDto>> ListMineAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var rows = await (from m in db.CompanyMembers.AsNoTracking()
                          join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id
                          where m.UserId == actorUserId && m.IsActive
                          orderby c.LegalName
                          select new { Company = c, m.Roles }).ToListAsync(ct);
        return rows.Select(r => new MyCompanyDto(ToDto(r.Company), r.Roles)).ToList();
    }

    public async Task<CompanyDto> GetAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await RequireMemberAsync(actorUserId, companyId, null, ct);
        return ToDto(await LoadCompanyAsync(companyId, ct));
    }

    // ---------- sites ----------

    public async Task<IReadOnlyList<SiteDto>> ListSitesAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await RequireMemberAsync(actorUserId, companyId, null, ct);
        var sites = await db.CompanySites.AsNoTracking().Where(s => s.CompanyId == companyId).OrderBy(s => s.Name).ToListAsync(ct);
        return sites.Select(ToDto).ToList();
    }

    public async Task<SiteDto> AddSiteAsync(Guid actorUserId, Guid companyId, SiteRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        // The first site is always the default delivery site.
        bool makeDefault = r.IsDefaultDelivery || !await db.CompanySites.AnyAsync(s => s.CompanyId == companyId, ct);
        var site = new CompanySite(Guid.NewGuid(), companyId, r.Name, r.ContactName, r.ContactPhone, r.Line1, r.Line2,
            r.Upazila, r.District, r.Division, r.PostalCode, false);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        db.CompanySites.Add(site);
        await db.SaveChangesAsync(ct);
        if (makeDefault) await SetDefaultSiteAsync(companyId, site, ct);
        await tx.CommitAsync(ct);
        return ToDto(site);
    }

    public async Task<SiteDto> UpdateSiteAsync(Guid actorUserId, Guid companyId, Guid siteId, SiteRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        var site = await db.CompanySites.FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId, ct)
                   ?? throw new KeyNotFoundException("Site not found.");

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        site.Update(r.Name, r.ContactName, r.ContactPhone, r.Line1, r.Line2, r.Upazila, r.District, r.Division, r.PostalCode);
        await db.SaveChangesAsync(ct);
        if (r.IsDefaultDelivery && !site.IsDefaultDelivery) await SetDefaultSiteAsync(companyId, site, ct);
        await tx.CommitAsync(ct);
        return ToDto(site);
    }

    public async Task RemoveSiteAsync(Guid actorUserId, Guid companyId, Guid siteId, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        var site = await db.CompanySites.FirstOrDefaultAsync(s => s.Id == siteId && s.CompanyId == companyId, ct)
                   ?? throw new KeyNotFoundException("Site not found.");
        // Read the flag from the database: ExecuteUpdate in SetDefaultSiteAsync bypasses the change tracker.
        bool isDefault = await db.CompanySites.AsNoTracking().AnyAsync(s => s.Id == siteId && s.IsDefaultDelivery, ct);
        if (isDefault && await db.CompanySites.CountAsync(s => s.CompanyId == companyId, ct) > 1)
            throw new ConflictException("DEFAULT_SITE_REQUIRED", "Choose another default delivery site before removing this one.");

        db.CompanySites.Remove(site);
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Clears the flag on the other sites first: the partial unique index allows only one default per company.</summary>
    private async Task SetDefaultSiteAsync(Guid companyId, CompanySite site, CancellationToken ct)
    {
        await db.CompanySites.Where(s => s.CompanyId == companyId && s.Id != site.Id && s.IsDefaultDelivery)
            .ExecuteUpdateAsync(u => u.SetProperty(s => s.IsDefaultDelivery, false), ct);
        site.SetDefaultDelivery(true);
        await db.SaveChangesAsync(ct);
    }

    // ---------- cost centres ----------

    public async Task<IReadOnlyList<CostCentreDto>> ListCostCentresAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await RequireMemberAsync(actorUserId, companyId, null, ct);
        var rows = await db.CostCentres.AsNoTracking().Where(c => c.CompanyId == companyId).OrderBy(c => c.Code).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<CostCentreDto> AddCostCentreAsync(Guid actorUserId, Guid companyId, CostCentreRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        var cc = new CostCentre(Guid.NewGuid(), companyId, r.Code, r.Name, r.MonthlyBudgetMinor);
        await EnsureCostCentreCodeFreeAsync(companyId, cc.Code, null, ct);
        db.CostCentres.Add(cc);
        await SaveCostCentreAsync(ct);
        return ToDto(cc);
    }

    public async Task<CostCentreDto> UpdateCostCentreAsync(Guid actorUserId, Guid companyId, Guid costCentreId, CostCentreRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        var cc = await db.CostCentres.FirstOrDefaultAsync(c => c.Id == costCentreId && c.CompanyId == companyId, ct)
                 ?? throw new KeyNotFoundException("Cost centre not found.");
        cc.Update(r.Code, r.Name, r.MonthlyBudgetMinor);
        await EnsureCostCentreCodeFreeAsync(companyId, cc.Code, cc.Id, ct);
        await SaveCostCentreAsync(ct);
        return ToDto(cc);
    }

    public async Task RemoveCostCentreAsync(Guid actorUserId, Guid companyId, Guid costCentreId, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        var cc = await db.CostCentres.FirstOrDefaultAsync(c => c.Id == costCentreId && c.CompanyId == companyId, ct)
                 ?? throw new KeyNotFoundException("Cost centre not found.");
        db.CostCentres.Remove(cc);
        await db.SaveChangesAsync(ct);

        // Members hold cost-centre ids in an array, so there is no FK to cascade: strip the id explicitly.
        var holders = await db.CompanyMembers.Where(m => m.CompanyId == companyId && m.CostCentreIds.Contains(costCentreId)).ToListAsync(ct);
        foreach (var m in holders) m.Update(m.Roles, m.SpendLimitMinor, m.CostCentreIds.Where(id => id != costCentreId), m.IsActive);
        if (holders.Count > 0) await db.SaveChangesAsync(ct);
    }

    private async Task EnsureCostCentreCodeFreeAsync(Guid companyId, string code, Guid? exceptId, CancellationToken ct)
    {
        if (await db.CostCentres.AnyAsync(c => c.CompanyId == companyId && c.Code == code && c.Id != exceptId, ct))
            throw new ConflictException("COST_CENTRE_CODE_EXISTS", "A cost centre with this code already exists.");
    }

    private async Task SaveCostCentreAsync(CancellationToken ct)
    {
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new ConflictException("COST_CENTRE_CODE_EXISTS", "A cost centre with this code already exists.");
        }
    }

    // ---------- members ----------

    public async Task<IReadOnlyList<MemberDto>> ListMembersAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await RequireMemberAsync(actorUserId, companyId, CompanyRoles.Admin, ct);
        var rows = await db.CompanyMembers.AsNoTracking().Where(m => m.CompanyId == companyId).OrderBy(m => m.CreatedAtUtc).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<MemberDto> AddMemberAsync(Guid actorUserId, Guid companyId, AddMemberRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        if (await db.CompanyMembers.AnyAsync(m => m.CompanyId == companyId && m.UserId == r.UserId, ct))
            throw new ConflictException("MEMBER_EXISTS", "This user is already a member of the company.");

        await EnsureCostCentresBelongAsync(companyId, r.CostCentreIds, ct);
        var member = new CompanyMember(Guid.NewGuid(), companyId, r.UserId, r.Roles, r.SpendLimitMinor, r.CostCentreIds);
        db.CompanyMembers.Add(member);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new ConflictException("MEMBER_EXISTS", "This user is already a member of the company.");
        }
        return ToDto(member);
    }

    public async Task<MemberDto> UpdateMemberAsync(Guid actorUserId, Guid companyId, Guid memberId, UpdateMemberRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        await EnsureCostCentresBelongAsync(companyId, r.CostCentreIds, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var member = await db.CompanyMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.CompanyId == companyId, ct)
                     ?? throw new KeyNotFoundException("Member not found.");
        bool losesAdmin = member.Has(CompanyRoles.Admin) && (!r.IsActive || (r.Roles & CompanyRoles.Admin) == 0);
        if (losesAdmin) await EnsureAnotherAdminAsync(companyId, member.Id, ct);

        member.Update(r.Roles, r.SpendLimitMinor, r.CostCentreIds, r.IsActive);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return ToDto(member);
    }

    public async Task RemoveMemberAsync(Guid actorUserId, Guid companyId, Guid memberId, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var member = await db.CompanyMembers.FirstOrDefaultAsync(m => m.Id == memberId && m.CompanyId == companyId, ct)
                     ?? throw new KeyNotFoundException("Member not found.");
        if (member.Has(CompanyRoles.Admin)) await EnsureAnotherAdminAsync(companyId, member.Id, ct);

        db.CompanyMembers.Remove(member);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
    }

    /// <summary>A company must always keep one active admin. The company row is locked so two admins cannot demote each other concurrently.</summary>
    private async Task EnsureAnotherAdminAsync(Guid companyId, Guid exceptMemberId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {companyId} FOR UPDATE", ct);
        bool other = await db.CompanyMembers.AnyAsync(
            m => m.CompanyId == companyId && m.Id != exceptMemberId && m.IsActive && (m.Roles & CompanyRoles.Admin) != 0, ct);
        if (!other) throw new ConflictException("LAST_ADMIN", "A company must keep at least one active admin.");
    }

    private async Task EnsureCostCentresBelongAsync(Guid companyId, IReadOnlyList<Guid>? ids, CancellationToken ct)
    {
        if (ids is not { Count: > 0 }) return;
        var distinct = ids.Distinct().ToList();
        int found = await db.CostCentres.CountAsync(c => c.CompanyId == companyId && distinct.Contains(c.Id), ct);
        if (found != distinct.Count) throw new ValidationException("costCentreIds", "One or more cost centres do not belong to this company.");
    }

    // ---------- staff ----------

    public async Task<PagedResult<CompanyDto>> AdminListAsync(string? status, int page, int pageSize, CancellationToken ct = default)
    {
        page = Math.Max(1, page);
        pageSize = Math.Clamp(pageSize, 1, MaxPageSize);
        var query = db.Companies.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(status))
        {
            if (!Enum.TryParse<CompanyStatus>(status, true, out var parsed) || !Enum.IsDefined(parsed))
                throw new ValidationException("status", "Unknown status.");
            query = query.Where(c => c.Status == parsed);
        }
        int total = await query.CountAsync(ct);
        var rows = await query.OrderByDescending(c => c.CreatedAtUtc).ThenBy(c => c.Id).Skip((page - 1) * pageSize).Take(pageSize).ToListAsync(ct);
        return new PagedResult<CompanyDto>(rows.Select(ToDto).ToList(), page, pageSize, total);
    }

    public async Task<CompanyDto> AdminGetAsync(Guid companyId, CancellationToken ct = default) =>
        ToDto(await LoadCompanyAsync(companyId, ct));

    public Task<CompanyDto> AdminActivateAsync(Guid companyId, CancellationToken ct = default) =>
        MutateCompanyAsync(companyId, c => c.Activate(), ct);

    public Task<CompanyDto> AdminSuspendAsync(Guid companyId, CancellationToken ct = default) =>
        MutateCompanyAsync(companyId, c => c.Suspend(), ct);

    public Task<CompanyDto> AdminSetCreditAsync(Guid companyId, SetCreditRequest r, CancellationToken ct = default) =>
        MutateCompanyAsync(companyId, c => c.SetCredit(r.Terms, Money.FromMinorUnits(r.LimitMinorUnits, c.Currency)), ct);

    private async Task<CompanyDto> MutateCompanyAsync(Guid companyId, Action<Company> change, CancellationToken ct)
    {
        var company = await LoadCompanyAsync(companyId, ct, track: true);
        change(company);
        await db.SaveChangesAsync(ct); // xmin token: a concurrent credit change surfaces as 409, never a lost update
        return ToDto(company);
    }

    // ---------- tenancy guards ----------

    /// <summary>
    /// Resolves the actor's active membership in the company. Not a member → 404 (existence is not revealed);
    /// member without the needed role → 403.
    /// </summary>
    private Task<CompanyMember> RequireMemberAsync(Guid actorUserId, Guid companyId, CompanyRoles? role, CancellationToken ct) =>
        CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, role, ct);

    private async Task RequireAdminWritableAsync(Guid actorUserId, Guid companyId, CancellationToken ct)
    {
        await RequireMemberAsync(actorUserId, companyId, CompanyRoles.Admin, ct);
        await CompanyAccess.RequireNotSuspendedAsync(db, companyId, ct);
    }

    private async Task<Company> LoadCompanyAsync(Guid companyId, CancellationToken ct, bool track = false)
    {
        var query = track ? db.Companies.AsQueryable() : db.Companies.AsNoTracking();
        return await query.FirstOrDefaultAsync(c => c.Id == companyId, ct) ?? throw new KeyNotFoundException("Company not found.");
    }

    // ---------- mapping ----------

    private static CompanyDto ToDto(Company c) => new(
        c.Id, c.LegalName, c.TradeLicenceNo, c.Bin, c.Tin, c.Industry,
        c.Status.ToString().ToLowerInvariant(), c.CreditTerms.ToString().ToLowerInvariant(),
        ToDto(c.CreditLimit), ToDto(c.CreditUsed), ToDto(c.CreditAvailable), c.CreatedAtUtc);

    private static MoneyDto ToDto(Money m) => new(m.ToMinorUnits(), m.Currency.ToString());

    private static SiteDto ToDto(CompanySite s) => new(s.Id, s.Name, s.ContactName, s.ContactPhone, s.Line1, s.Line2,
        s.Upazila, s.District, s.Division, s.PostalCode, s.IsDefaultDelivery);

    private static CostCentreDto ToDto(CostCentre c) => new(c.Id, c.Code, c.Name, c.MonthlyBudgetMinor);

    private static MemberDto ToDto(CompanyMember m) => new(m.Id, m.UserId, m.Roles, m.SpendLimitMinor, m.IsActive, m.CostCentreIds);
}
