using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Approvals;
using ShopVerse.Application.Companies;
using ShopVerse.Domain.Approvals;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Companies;
using ShopVerse.Infrastructure.Approvals;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Companies;

public class ApprovalServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _binSeq = 2_000_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 3_000_000_000_000L).ToString("D13");

    private sealed record Team(Guid Company, Guid Admin, Guid Requester, Guid Approver1, Guid Approver2);

    private (ShopVerseDbContext Db, ApprovalService Svc) Open()
    {
        var db = fixture.CreateContext();
        return (db, new ApprovalService(db, TimeProvider.System));
    }

    private async Task<Team> SeedTeamAsync()
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var (requester, a1, a2) = (await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture));
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Approvals Ltd", "TL-A-1", NextBin(), null, null))).Id;
        await companies.AddMemberAsync(admin, company, new AddMemberRequest(requester, CompanyRoles.Requester, null, null));
        await companies.AddMemberAsync(admin, company, new AddMemberRequest(a1, CompanyRoles.Approver, null, null));
        await companies.AddMemberAsync(admin, company, new AddMemberRequest(a2, CompanyRoles.Approver, null, null));
        return new Team(company, admin, requester, a1, a2);
    }

    private async Task<Guid> AddRuleAsync(Team t, long min, params Guid[] chain)
    {
        var (db, svc) = Open();
        await using var _ = db;
        return (await svc.AddRuleAsync(t.Admin, t.Company, new ApprovalRuleRequest("Rule " + min, min, null, null, chain))).Id;
    }

    private static SubmitApprovalRequest Subject(long amount = 500_000, Guid? id = null) =>
        new("order", id ?? Guid.NewGuid(), amount, null, null);

    [PostgresFact]
    public async Task Submit_WithoutAMatchingRule_NeedsNoApproval_AndStoresNothing()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 1_000_000, t.Approver1);
        var (db, svc) = Open();
        await using var _ = db;

        var result = await svc.SubmitAsync(t.Requester, t.Company, Subject(999_999));
        result.ApprovalRequired.Should().BeFalse();
        (await svc.ListMineAsync(t.Requester, t.Company)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task FullChain_TwoApprovers_InOrder_EndsApproved()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 100_000, t.Approver1, t.Approver2);
        var (db, svc) = Open();
        await using var _ = db;

        var created = (await svc.SubmitAsync(t.Requester, t.Company, Subject())).Request!;
        created.Status.Should().Be("pending");
        created.CurrentApproverUserId.Should().Be(t.Approver1);

        (await svc.ListInboxAsync(t.Approver1, t.Company)).Should().ContainSingle(r => r.Id == created.Id);
        (await svc.ListInboxAsync(t.Approver2, t.Company)).Should().BeEmpty("it is not approver 2's turn yet");

        await Assert.ThrowsAsync<ForbiddenException>(() => svc.DecideAsync(t.Approver2, t.Company, created.Id, new DecisionRequest(true, null)));
        var afterFirst = await svc.DecideAsync(t.Approver1, t.Company, created.Id, new DecisionRequest(true, "ok"));
        afterFirst.Status.Should().Be("pending");
        (await svc.ListInboxAsync(t.Approver2, t.Company)).Should().ContainSingle();
        (await svc.ListInboxAsync(t.Approver1, t.Company)).Should().BeEmpty();

        var done = await svc.DecideAsync(t.Approver2, t.Company, created.Id, new DecisionRequest(true, null));
        done.Status.Should().Be("approved");
        done.Steps.Should().OnlyContain(s => s.Decision == "approved");
    }

    [PostgresFact]
    public async Task Rejection_NeedsAComment_AndIsFinal()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 0, t.Approver1, t.Approver2);
        var (db, svc) = Open();
        await using var _ = db;
        var req = (await svc.SubmitAsync(t.Requester, t.Company, Subject())).Request!;

        await Assert.ThrowsAsync<ValidationException>(() => svc.DecideAsync(t.Approver1, t.Company, req.Id, new DecisionRequest(false, "")));
        var rejected = await svc.DecideAsync(t.Approver1, t.Company, req.Id, new DecisionRequest(false, "Over budget"));
        rejected.Status.Should().Be("rejected");
        (await Assert.ThrowsAsync<ConflictException>(() => svc.DecideAsync(t.Approver2, t.Company, req.Id, new DecisionRequest(true, null)))).Code.Should().Be("APPROVAL_NOT_PENDING");
    }

    [PostgresFact]
    public async Task Requester_CannotApproveOwnRequest_EvenAsAdminAndInTheChain()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 0, t.Admin, t.Approver1);
        var (db, svc) = Open();
        await using var _ = db;

        var req = (await svc.SubmitAsync(t.Admin, t.Company, Subject())).Request!;
        req.Steps.Should().ContainSingle(s => s.ApproverUserId == t.Approver1, "the requester was skipped in the chain");
        (await Assert.ThrowsAsync<ForbiddenException>(() => svc.DecideAsync(t.Admin, t.Company, req.Id, new DecisionRequest(true, null)))).Code.Should().Be("SELF_APPROVAL");
    }

    [PostgresFact]
    public async Task SameItem_CannotHaveTwoPendingRequests_UntilTheFirstEnds()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 0, t.Approver1);
        var (db, svc) = Open();
        await using var _ = db;
        var subject = Subject(id: Guid.NewGuid());

        var first = (await svc.SubmitAsync(t.Requester, t.Company, subject)).Request!;
        (await Assert.ThrowsAsync<ConflictException>(() => svc.SubmitAsync(t.Requester, t.Company, subject))).Code.Should().Be("APPROVAL_ALREADY_PENDING");

        await svc.CancelAsync(t.Requester, t.Company, first.Id);
        (await svc.SubmitAsync(t.Requester, t.Company, subject)).Request.Should().NotBeNull("a cancelled request frees the item");
    }

    [PostgresFact]
    public async Task ConcurrentSubmitOfTheSameItem_CreatesExactlyOneRequest()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 0, t.Approver1);
        var subject = Subject(id: Guid.NewGuid());
        using var gate = new ManualResetEventSlim(false);

        var tasks = Enumerable.Range(0, 8).Select(_ => Task.Run(async () =>
        {
            var (db, svc) = Open();
            await using var __ = db;
            gate.Wait();
            try { await svc.SubmitAsync(t.Requester, t.Company, subject); return true; }
            catch (ConflictException) { return false; }
        })).ToList();
        gate.Set();
        (await Task.WhenAll(tasks)).Count(x => x).Should().Be(1);

        await using var check = fixture.CreateContext();
        (await check.ApprovalRequests.CountAsync(r => r.CompanyId == t.Company && r.SubjectId == subject.SubjectId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task ConcurrentDecisions_ByTheSameApprover_OnlyOneWins_AndThereIsNoDoubleApproval()
    {
        for (int round = 0; round < 8; round++)
        {
            var t = await SeedTeamAsync();
            await AddRuleAsync(t, 0, t.Approver1, t.Approver2);
            Guid id;
            await using (var db0 = fixture.CreateContext())
                id = (await new ApprovalService(db0, TimeProvider.System).SubmitAsync(t.Requester, t.Company, Subject())).Request!.Id;

            using var gate = new ManualResetEventSlim(false);
            var tasks = Enumerable.Range(0, 6).Select(_ => Task.Run(async () =>
            {
                var (db, svc) = Open();
                await using var __ = db;
                gate.Wait();
                try { await svc.DecideAsync(t.Approver1, t.Company, id, new DecisionRequest(true, null)); return true; }
                catch (Exception ex) when (ex is ConflictException or ForbiddenException or DbUpdateException) { return false; }
            })).ToList();
            gate.Set();
            (await Task.WhenAll(tasks)).Count(x => x).Should().Be(1, $"round {round}: one approval counts once");

            await using var check = fixture.CreateContext();
            var req = await check.ApprovalRequests.Include(r => r.Steps).AsNoTracking().SingleAsync(r => r.Id == id);
            req.Steps.Count(s => s.Decision == StepDecision.Approved).Should().Be(1, $"round {round}");
            req.Status.Should().Be(ApprovalStatus.Pending, "approver 2 has not decided");
        }
    }

    [PostgresFact]
    public async Task Idor_OutsiderAndOtherCompanyAdmin_SeeNothing_EveryOperationIsNotFound()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 0, t.Approver1);
        var other = await SeedTeamAsync();
        var (db0, svc0) = Open();
        Guid ruleId, reqId;
        await using (db0)
        {
            ruleId = (await svc0.ListRulesAsync(t.Admin, t.Company)).Single().Id;
            reqId = (await svc0.SubmitAsync(t.Requester, t.Company, Subject())).Request!.Id;
        }

        var (db, svc) = Open();
        await using var _ = db;
        var outsider = other.Admin; // a fully privileged admin, but of a different company
        var attempts = new Dictionary<string, Func<Task>>
        {
            ["listRules"] = () => svc.ListRulesAsync(outsider, t.Company),
            ["addRule"] = () => svc.AddRuleAsync(outsider, t.Company, new ApprovalRuleRequest("x", 0, null, null, [t.Approver1])),
            ["updateRule"] = () => svc.UpdateRuleAsync(outsider, t.Company, ruleId, new ApprovalRuleRequest("x", 0, null, null, [t.Approver1])),
            ["removeRule"] = () => svc.RemoveRuleAsync(outsider, t.Company, ruleId),
            ["submit"] = () => svc.SubmitAsync(outsider, t.Company, Subject()),
            ["get"] = () => svc.GetAsync(outsider, t.Company, reqId),
            ["inbox"] = () => svc.ListInboxAsync(outsider, t.Company),
            ["mine"] = () => svc.ListMineAsync(outsider, t.Company),
            ["decide"] = () => svc.DecideAsync(outsider, t.Company, reqId, new DecisionRequest(true, null)),
            ["cancel"] = () => svc.CancelAsync(outsider, t.Company, reqId),
        };
        foreach (var (name, attempt) in attempts)
            await Assert.ThrowsAsync<KeyNotFoundException>(attempt).ContinueWith(x => x.Result.Message.Should().Be("Company not found.", name));

        // The request id used through another company's route is also unknown.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetAsync(other.Admin, other.Company, reqId));
        await using var check = fixture.CreateContext();
        (await check.ApprovalRequests.AsNoTracking().SingleAsync(r => r.Id == reqId)).Status.Should().Be(ApprovalStatus.Pending);
    }

    [PostgresFact]
    public async Task InsideTheCompany_RolesAndVisibilityAreEnforced()
    {
        var t = await SeedTeamAsync();
        var bystander = await TestUsers.CreateAsync(fixture);
        await using (var dbm = fixture.CreateContext())
            await new CompanyService(dbm).AddMemberAsync(t.Admin, t.Company, new AddMemberRequest(bystander, CompanyRoles.Buyer, null, null));
        await AddRuleAsync(t, 0, t.Approver1);
        var (db, svc) = Open();
        await using var _ = db;
        var req = (await svc.SubmitAsync(t.Requester, t.Company, Subject())).Request!;

        // Only admins manage rules.
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListRulesAsync(t.Requester, t.Company));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.AddRuleAsync(t.Approver1, t.Company, new ApprovalRuleRequest("x", 0, null, null, [t.Approver1])));
        // A buyer cannot approve or see an inbox.
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListInboxAsync(bystander, t.Company));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.DecideAsync(bystander, t.Company, req.Id, new DecisionRequest(true, null)));
        // A member who is not involved cannot even see the request; the requester, the approver and an admin can.
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetAsync(bystander, t.Company, req.Id));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.DecideAsync(t.Approver2, t.Company, req.Id, new DecisionRequest(true, null)));
        (await svc.GetAsync(t.Requester, t.Company, req.Id)).Id.Should().Be(req.Id);
        (await svc.GetAsync(t.Approver1, t.Company, req.Id)).Id.Should().Be(req.Id);
        (await svc.GetAsync(t.Admin, t.Company, req.Id)).Id.Should().Be(req.Id);
        // Only the requester cancels.
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.CancelAsync(t.Approver1, t.Company, req.Id));
        (await svc.CancelAsync(t.Requester, t.Company, req.Id)).Status.Should().Be("cancelled");
    }

    [PostgresFact]
    public async Task Rules_RejectForeignApproversAndCostCentres()
    {
        var t = await SeedTeamAsync();
        var other = await SeedTeamAsync();
        var (db, svc) = Open();
        await using var _ = db;

        // approver from another company, a requester-only member, and a random user
        await Assert.ThrowsAsync<ValidationException>(() => svc.AddRuleAsync(t.Admin, t.Company, new ApprovalRuleRequest("x", 0, null, null, [other.Approver1])));
        await Assert.ThrowsAsync<ValidationException>(() => svc.AddRuleAsync(t.Admin, t.Company, new ApprovalRuleRequest("x", 0, null, null, [t.Requester])));
        await Assert.ThrowsAsync<ValidationException>(() => svc.AddRuleAsync(t.Admin, t.Company, new ApprovalRuleRequest("x", 0, null, null, [Guid.NewGuid()])));
        // a cost centre of another company
        Guid foreignCc;
        await using (var dbc = fixture.CreateContext())
            foreignCc = (await new CompanyService(dbc).AddCostCentreAsync(other.Admin, other.Company, new CostCentreRequest("X1", "X", null))).Id;
        await Assert.ThrowsAsync<ValidationException>(() => svc.AddRuleAsync(t.Admin, t.Company, new ApprovalRuleRequest("x", 0, foreignCc, null, [t.Approver1])));
        (await svc.ListRulesAsync(t.Admin, t.Company)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task ApproverWhoLeftTheCompany_IsSkippedInNewRequests_AndRuleWithNoOneLeftIsAConflict()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 0, t.Approver1, t.Approver2);
        await using (var dbm = fixture.CreateContext())
        {
            var svcc = new CompanyService(dbm);
            var m = (await svcc.ListMembersAsync(t.Admin, t.Company)).Single(x => x.UserId == t.Approver1);
            await svcc.RemoveMemberAsync(t.Admin, t.Company, m.Id);
        }
        var (db, svc) = Open();
        await using var _ = db;

        var req = (await svc.SubmitAsync(t.Requester, t.Company, Subject())).Request!;
        req.Steps.Should().ContainSingle(s => s.ApproverUserId == t.Approver2);

        await using (var dbm2 = fixture.CreateContext())
        {
            var svcc = new CompanyService(dbm2);
            var m = (await svcc.ListMembersAsync(t.Admin, t.Company)).Single(x => x.UserId == t.Approver2);
            await svcc.RemoveMemberAsync(t.Admin, t.Company, m.Id);
        }
        (await Assert.ThrowsAsync<ConflictException>(() => svc.SubmitAsync(t.Requester, t.Company, Subject()))).Code.Should().Be("NO_ELIGIBLE_APPROVER");
    }

    [PostgresFact]
    public async Task SuspendedCompany_BlocksChanges_ButNotReading()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 0, t.Approver1);
        var (db, svc) = Open();
        await using var _ = db;
        var req = (await svc.SubmitAsync(t.Requester, t.Company, Subject())).Request!;
        await using (var dbs = fixture.CreateContext())
        {
            var c = await dbs.Companies.SingleAsync(x => x.Id == t.Company);
            c.Suspend();
            await dbs.SaveChangesAsync();
        }

        (await Assert.ThrowsAsync<ConflictException>(() => svc.DecideAsync(t.Approver1, t.Company, req.Id, new DecisionRequest(true, null)))).Code.Should().Be("COMPANY_SUSPENDED");
        (await Assert.ThrowsAsync<ConflictException>(() => svc.SubmitAsync(t.Requester, t.Company, Subject()))).Code.Should().Be("COMPANY_SUSPENDED");
        (await svc.GetAsync(t.Requester, t.Company, req.Id)).Status.Should().Be("pending");
    }

    [PostgresFact]
    public async Task RuleSelection_UsesTheHighestMatchingThreshold()
    {
        var t = await SeedTeamAsync();
        await AddRuleAsync(t, 10_000, t.Approver1);
        await AddRuleAsync(t, 1_000_000, t.Approver2);
        var (db, svc) = Open();
        await using var _ = db;

        var small = (await svc.SubmitAsync(t.Requester, t.Company, Subject(50_000))).Request!;
        var large = (await svc.SubmitAsync(t.Requester, t.Company, Subject(2_000_000))).Request!;
        small.CurrentApproverUserId.Should().Be(t.Approver1);
        large.CurrentApproverUserId.Should().Be(t.Approver2);
    }
}
