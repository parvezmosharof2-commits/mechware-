using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Companies;

public sealed class CompanyService(MechwareDbContext db) : ICompanyService
{
    private const int MaxPageSize = 100;

    // ---------- registration & reads ----------

    public async Task<CompanyDto> RegisterAsync(Guid actorUserId, RegisterCompanyRequest request, CancellationToken ct = default)
    {
        await EnsureUserExistsAsync(actorUserId, ct);
        var company = Company.Register(Guid.NewGuid(), request.LegalName, request.TradeLicenceNo, request.Bin, request.Tin, request.Industry);

        // One account cannot hold a stack of unverified registrations (and their BINs) at once (threat model v1, H2). The
        // transaction-scoped lock per registrant makes the count exact under parallel requests.
        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var lockKey = $"company-register:{actorUserId:N}";
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_advisory_xact_lock(hashtextextended({lockKey}, 0))", ct);
        // Accepted only: otherwise anyone could block a person's registrations by inviting them as admin.
        var pending = await (from m in db.CompanyMembers.Effective()
                             join c in db.Companies on m.CompanyId equals c.Id
                             where m.UserId == actorUserId && (m.Roles & CompanyRoles.Admin) != 0
                                   && c.Status == CompanyStatus.Pending
                             select c.Id).CountAsync(ct);
        if (pending >= MaxPendingRegistrationsPerUser)
            throw new ConflictException("TOO_MANY_PENDING_COMPANIES",
                $"You already have {pending} companies waiting for verification. Wait for MECHWARE to verify them first.");

        // Every configured key: during a key rotation, rows not yet rewritten carry the fingerprint under the old key.
        var binIndexes = CompanyBinIndex.AllOf(company.Bin);
        if (await db.Companies.AnyAsync(c => binIndexes.Contains(EF.Property<string>(c, CompanyBinIndex.Property)) && c.Status != CompanyStatus.Rejected, ct))
            throw new ConflictException("COMPANY_BIN_EXISTS", "A company with this BIN is already registered.");

        db.Companies.Add(company);
        // The registrant becomes the first Company Admin; the company stays Pending until MECHWARE verifies it.
        db.CompanyMembers.Add(new CompanyMember(Guid.NewGuid(), company.Id, actorUserId, CompanyRoles.Admin, null, null, accepted: true));

        try
        {
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new ConflictException("COMPANY_BIN_EXISTS", "A company with this BIN is already registered.");
        }

        return ToDto(company);
    }

    public async Task<IReadOnlyList<MyCompanyDto>> ListMineAsync(Guid actorUserId, CancellationToken ct = default)
    {
        var rows = await (from m in db.CompanyMembers.AsNoTracking().Effective()
                          join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id
                          where m.UserId == actorUserId
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

    /// <summary>A membership must name a live account: not a made-up id, and not one that was erased.</summary>
    private async Task EnsureUserExistsAsync(Guid userId, CancellationToken ct)
    {
        if (!await db.Users.AnyAsync(u => u.Id == userId && u.DeletedAtUtc == null, ct))
            throw new ValidationException("userId", "No active account exists for this user ID.");
    }

    public async Task<MemberDto> AddMemberAsync(Guid actorUserId, Guid companyId, AddMemberRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        await EnsureUserExistsAsync(r.UserId, ct);
        if (await db.CompanyMembers.AnyAsync(m => m.CompanyId == companyId && m.UserId == r.UserId, ct))
            throw new ConflictException("MEMBER_EXISTS", "This user is already a member of the company.");
        var declinedSince = DateTime.UtcNow - CompanyInvitationDecline.CoolingOff;
        if (await db.CompanyInvitationDeclines.AnyAsync(
                d => d.CompanyId == companyId && d.UserId == r.UserId && d.DeclinedAtUtc > declinedSince, ct))
            throw new ConflictException("INVITATION_DECLINED", "This person recently declined an invitation from this company.");

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

        // Switching an unanswered invitation off withdraws it, so it neither lingers out of the invitee's sight nor blocks a
        // later invitation.
        if (!member.IsAccepted && !r.IsActive)
        {
            db.CompanyMembers.Remove(member);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return ToDto(member) with { IsActive = false };
        }

        // Consent covers the roles that were offered: a new role (say, Approver) is offered again and needs a new "yes". An
        // admin changing their own roles has consented by doing it (and the company keeps the acting admin either way).
        bool gainsRoles = member.IsAccepted && member.UserId != actorUserId && (r.Roles & ~member.Roles) != 0;
        member.Update(r.Roles, r.SpendLimitMinor, r.CostCentreIds, r.IsActive);
        if (gainsRoles) member.Reinvite(DateTime.UtcNow);
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

    // ---------- invitations (the invited person's side) ----------

    public async Task<IReadOnlyList<InvitationDto>> ListInvitationsAsync(Guid actorUserId, CancellationToken ct = default) =>
        await (from m in db.CompanyMembers.AsNoTracking()
               join c in db.Companies.AsNoTracking() on m.CompanyId equals c.Id
               where m.UserId == actorUserId && m.AcceptedAtUtc == null && m.IsActive && c.Status != CompanyStatus.Rejected
               orderby m.CreatedAtUtc
               select new InvitationDto(c.Id, c.LegalName, m.Roles, m.CreatedAtUtc)).ToListAsync(ct);

    public async Task<MyCompanyDto> AcceptInvitationAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        // Only the invited person can accept, and only their own invitation: anything else reads as "not found".
        var member = await db.CompanyMembers.FirstOrDefaultAsync(
                         m => m.CompanyId == companyId && m.UserId == actorUserId && m.AcceptedAtUtc == null && m.IsActive, ct)
                     ?? throw new KeyNotFoundException("Invitation not found.");
        var company = await LoadCompanyAsync(companyId, ct);
        if (company.Status == CompanyStatus.Rejected)
            throw new ConflictException("COMPANY_REJECTED", "MECHWARE did not accept this company's registration.");

        member.Accept(DateTime.UtcNow);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (DbUpdateConcurrencyException)
        {
            // The admin withdrew or changed the invitation at the same moment: what was accepted no longer exists as offered.
            throw new KeyNotFoundException("Invitation not found.");
        }
        return new MyCompanyDto(ToDto(company), member.Roles);
    }

    public async Task DeclineInvitationAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        var member = await db.CompanyMembers.FirstOrDefaultAsync(
                         m => m.CompanyId == companyId && m.UserId == actorUserId && m.AcceptedAtUtc == null, ct)
                     ?? throw new KeyNotFoundException("Invitation not found.");
        db.CompanyMembers.Remove(member);

        // Remember the "no" so the company cannot simply ask again (see CompanyInvitationDecline).
        var now = DateTime.UtcNow;
        var decline = await db.CompanyInvitationDeclines.FindAsync([companyId, actorUserId], ct);
        if (decline is null) db.CompanyInvitationDeclines.Add(new CompanyInvitationDecline(companyId, actorUserId, now));
        else decline.Renew(now);
        try
        {
            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex) when (ex is DbUpdateConcurrencyException
                                   || ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // A second "decline" (double click) or the admin withdrawing at the same moment: the invitation is already gone.
            throw new KeyNotFoundException("Invitation not found.");
        }
    }

    /// <summary>A company must always keep one active admin. The company row is locked so two admins cannot demote each other concurrently.</summary>
    private async Task EnsureAnotherAdminAsync(Guid companyId, Guid exceptMemberId, CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {companyId} FOR UPDATE", ct);
        bool other = await db.CompanyMembers.Effective().AnyAsync(
            m => m.CompanyId == companyId && m.Id != exceptMemberId && (m.Roles & CompanyRoles.Admin) != 0, ct);
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

    /// <summary>Unverified registrations one account may hold at a time.</summary>
    public const int MaxPendingRegistrationsPerUser = 2;

    public Task<CompanyDto> AdminRejectAsync(Guid companyId, RejectCompanyRequest r, CancellationToken ct = default) =>
        MutateCompanyAsync(companyId, c => c.Reject(r.Reason), ct);

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
        ToDto(c.CreditLimit), ToDto(c.CreditUsed), ToDto(c.CreditAvailable), c.CreatedAtUtc, c.RejectionReason);

    private static MoneyDto ToDto(Money m) => new(m.ToMinorUnits(), m.Currency.ToString());

    private static SiteDto ToDto(CompanySite s) => new(s.Id, s.Name, s.ContactName, s.ContactPhone, s.Line1, s.Line2,
        s.Upazila, s.District, s.Division, s.PostalCode, s.IsDefaultDelivery);

    private static CostCentreDto ToDto(CostCentre c) => new(c.Id, c.Code, c.Name, c.MonthlyBudgetMinor);

    private static MemberDto ToDto(CompanyMember m) => new(m.Id, m.UserId, m.Roles, m.SpendLimitMinor, m.IsActive, m.CostCentreIds, m.IsAccepted);
}
