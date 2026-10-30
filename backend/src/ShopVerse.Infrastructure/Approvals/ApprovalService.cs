using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Approvals;
using ShopVerse.Domain.Approvals;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Companies;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Approvals;

public sealed class ApprovalService(ShopVerseDbContext db, TimeProvider clock) : IApprovalService
{
    private const CompanyRoles AnyRole = CompanyRoles.Requester | CompanyRoles.Buyer | CompanyRoles.Approver | CompanyRoles.Admin;
    private const CompanyRoles CanApprove = CompanyRoles.Approver | CompanyRoles.Admin;

    // ---------- rules (company admins) ----------

    public async Task<IReadOnlyList<ApprovalRuleDto>> ListRulesAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, CompanyRoles.Admin, ct);
        var rules = await db.ApprovalRules.AsNoTracking().Where(r => r.CompanyId == companyId)
            .OrderByDescending(r => r.MinAmountMinor).ThenBy(r => r.Name).ToListAsync(ct);
        return rules.Select(ToDto).ToList();
    }

    public async Task<ApprovalRuleDto> AddRuleAsync(Guid actorUserId, Guid companyId, ApprovalRuleRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        await EnsureRuleReferencesAsync(companyId, r, ct);
        var rule = new ApprovalRule(Guid.NewGuid(), companyId, r.Name, r.MinAmountMinor, r.CostCentreId, r.CategoryIds, r.ApproverUserIds, r.IsActive);
        db.ApprovalRules.Add(rule);
        await db.SaveChangesAsync(ct);
        return ToDto(rule);
    }

    public async Task<ApprovalRuleDto> UpdateRuleAsync(Guid actorUserId, Guid companyId, Guid ruleId, ApprovalRuleRequest r, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        var rule = await db.ApprovalRules.FirstOrDefaultAsync(x => x.Id == ruleId && x.CompanyId == companyId, ct)
                   ?? throw new KeyNotFoundException("Rule not found.");
        await EnsureRuleReferencesAsync(companyId, r, ct);
        rule.Update(r.Name, r.MinAmountMinor, r.CostCentreId, r.CategoryIds, r.ApproverUserIds, r.IsActive);
        await db.SaveChangesAsync(ct);
        return ToDto(rule);
    }

    public async Task RemoveRuleAsync(Guid actorUserId, Guid companyId, Guid ruleId, CancellationToken ct = default)
    {
        await RequireAdminWritableAsync(actorUserId, companyId, ct);
        var rule = await db.ApprovalRules.FirstOrDefaultAsync(x => x.Id == ruleId && x.CompanyId == companyId, ct)
                   ?? throw new KeyNotFoundException("Rule not found.");
        db.ApprovalRules.Remove(rule); // open requests keep their own snapshot of the rule name and chain
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Approvers must be active members who can approve; the cost centre must belong to this company.</summary>
    private async Task EnsureRuleReferencesAsync(Guid companyId, ApprovalRuleRequest r, CancellationToken ct)
    {
        var approvers = (r.ApproverUserIds ?? []).Distinct().ToList();
        if (approvers.Count > 0)
        {
            var eligible = await db.CompanyMembers.AsNoTracking()
                .Where(m => m.CompanyId == companyId && m.IsActive && approvers.Contains(m.UserId)
                            && ((m.Roles & CompanyRoles.Approver) != 0 || (m.Roles & CompanyRoles.Admin) != 0))
                .Select(m => m.UserId).ToListAsync(ct);
            if (eligible.Count != approvers.Count)
                throw new ValidationException("approverUserIds", "Every approver must be an active member of this company with the Approver or Admin role.");
        }
        if (r.CostCentreId is { } cc && !await db.CostCentres.AnyAsync(c => c.Id == cc && c.CompanyId == companyId, ct))
            throw new ValidationException("costCentreId", "The cost centre does not belong to this company.");
    }

    // ---------- requests ----------

    public async Task<SubmitApprovalResult> SubmitAsync(Guid actorUserId, Guid companyId, SubmitApprovalRequest r, CancellationToken ct = default)
    {
        await CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, AnyRole, ct);
        await CompanyAccess.RequireNotSuspendedAsync(db, companyId, ct);

        if (r.CostCentreId is { } cc && !await db.CostCentres.AnyAsync(c => c.Id == cc && c.CompanyId == companyId, ct))
            throw new ValidationException("costCentreId", "The cost centre does not belong to this company.");
        if (r.AmountMinor <= 0) throw new ValidationException("amount", "Amount must be positive.");

        var categories = r.CategoryIds?.Distinct().ToList() ?? [];
        var rules = await db.ApprovalRules.AsNoTracking().Where(x => x.CompanyId == companyId && x.IsActive).ToListAsync(ct);
        var rule = ApprovalRule.Select(rules, r.AmountMinor, r.CostCentreId, categories);
        if (rule is null) return new SubmitApprovalResult(false, null);

        var eligible = (await db.CompanyMembers.AsNoTracking()
            .Where(m => m.CompanyId == companyId && m.IsActive && rule.ApproverUserIds.Contains(m.UserId)
                        && ((m.Roles & CompanyRoles.Approver) != 0 || (m.Roles & CompanyRoles.Admin) != 0))
            .Select(m => m.UserId).ToListAsync(ct)).ToHashSet();

        var request = ApprovalRequest.Create(Guid.NewGuid(), actorUserId, r.SubjectType, r.SubjectId, r.AmountMinor, r.CostCentreId, categories, rule, eligible);
        if (await db.ApprovalRequests.AnyAsync(x => x.CompanyId == companyId && x.SubjectType == request.SubjectType
                && x.SubjectId == request.SubjectId && x.Status == ApprovalStatus.Pending, ct))
            throw new ConflictException("APPROVAL_ALREADY_PENDING", "An approval for this item is already pending.");

        db.ApprovalRequests.Add(request);
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            throw new ConflictException("APPROVAL_ALREADY_PENDING", "An approval for this item is already pending.");
        }
        return new SubmitApprovalResult(true, ToDto(request));
    }

    public async Task<ApprovalRequestDto> GetAsync(Guid actorUserId, Guid companyId, Guid requestId, CancellationToken ct = default)
    {
        var member = await CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, AnyRole, ct);
        var request = await db.ApprovalRequests.AsNoTracking().Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == requestId && x.CompanyId == companyId, ct);
        if (request is null || !CanSee(request, actorUserId, member)) throw new KeyNotFoundException("Approval not found.");
        return ToDto(request);
    }

    public async Task<IReadOnlyList<ApprovalRequestDto>> ListInboxAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, CanApprove, ct);
        var candidates = await db.ApprovalRequests.AsNoTracking().Include(x => x.Steps)
            .Where(x => x.CompanyId == companyId && x.Status == ApprovalStatus.Pending
                        && x.Steps.Any(s => s.ApproverUserId == actorUserId && s.Decision == StepDecision.Pending))
            .OrderBy(x => x.CreatedAtUtc).ToListAsync(ct);
        return candidates.Where(x => x.CurrentStep?.ApproverUserId == actorUserId).Select(ToDto).ToList();
    }

    public async Task<IReadOnlyList<ApprovalRequestDto>> ListMineAsync(Guid actorUserId, Guid companyId, CancellationToken ct = default)
    {
        await CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, AnyRole, ct);
        var rows = await db.ApprovalRequests.AsNoTracking().Include(x => x.Steps)
            .Where(x => x.CompanyId == companyId && x.RequesterUserId == actorUserId)
            .OrderByDescending(x => x.CreatedAtUtc).Take(200).ToListAsync(ct);
        return rows.Select(ToDto).ToList();
    }

    public async Task<ApprovalRequestDto> DecideAsync(Guid actorUserId, Guid companyId, Guid requestId, DecisionRequest d, CancellationToken ct = default)
    {
        var member = await CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, CanApprove, ct);
        await CompanyAccess.RequireNotSuspendedAsync(db, companyId, ct);
        var request = await LoadVisibleAsync(actorUserId, companyId, requestId, member, ct);
        request.Decide(actorUserId, d.Approve, d.Comment, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct); // xmin token: a concurrent decision on the same request surfaces as 409
        return ToDto(request);
    }

    public async Task<ApprovalRequestDto> CancelAsync(Guid actorUserId, Guid companyId, Guid requestId, CancellationToken ct = default)
    {
        var member = await CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, AnyRole, ct);
        await CompanyAccess.RequireNotSuspendedAsync(db, companyId, ct);
        var request = await LoadVisibleAsync(actorUserId, companyId, requestId, member, ct);
        request.Cancel(actorUserId, clock.GetUtcNow().UtcDateTime);
        await db.SaveChangesAsync(ct);
        return ToDto(request);
    }

    /// <summary>A request is visible to its requester, to anyone in its chain, and to company admins. Others get "not found".</summary>
    private async Task<ApprovalRequest> LoadVisibleAsync(Guid actorUserId, Guid companyId, Guid requestId, CompanyMember member, CancellationToken ct)
    {
        var request = await db.ApprovalRequests.Include(x => x.Steps)
            .FirstOrDefaultAsync(x => x.Id == requestId && x.CompanyId == companyId, ct);
        if (request is null || !CanSee(request, actorUserId, member)) throw new KeyNotFoundException("Approval not found.");
        return request;
    }

    private static bool CanSee(ApprovalRequest r, Guid actorUserId, CompanyMember member) =>
        r.RequesterUserId == actorUserId || member.Has(CompanyRoles.Admin) || r.Steps.Any(s => s.ApproverUserId == actorUserId);

    private async Task RequireAdminWritableAsync(Guid actorUserId, Guid companyId, CancellationToken ct)
    {
        await CompanyAccess.RequireMemberAsync(db, actorUserId, companyId, CompanyRoles.Admin, ct);
        await CompanyAccess.RequireNotSuspendedAsync(db, companyId, ct);
    }

    // ---------- mapping ----------

    private static ApprovalRuleDto ToDto(ApprovalRule r) =>
        new(r.Id, r.Name, r.MinAmountMinor, r.CostCentreId, r.CategoryIds, r.ApproverUserIds, r.IsActive);

    private static ApprovalRequestDto ToDto(ApprovalRequest r) => new(
        r.Id, r.RequesterUserId, r.SubjectType, r.SubjectId, r.AmountMinor, r.CostCentreId, r.CategoryIds, r.RuleId, r.RuleName,
        r.Status.ToString().ToLowerInvariant(), r.CurrentStep?.ApproverUserId,
        r.Steps.OrderBy(s => s.Position).Select(s => new ApprovalStepDto(s.Position, s.ApproverUserId, s.Decision.ToString().ToLowerInvariant(), s.Comment, s.DecidedAtUtc)).ToList(),
        r.CreatedAtUtc);
}
