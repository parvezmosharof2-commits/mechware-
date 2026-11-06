using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Approvals;
using ShopVerse.Application.Companies;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Companies;
using ShopVerse.Infrastructure.Approvals;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Companies;

/// <summary>
/// Threat model v1, M2: a company admin could attach anyone to their company, even as an approver, and the person could not
/// refuse. A membership is now an invitation until its holder accepts it, and an invitation grants nothing.
/// </summary>
public class MemberInvitationTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _binSeq = 3_000_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 3_000_000_000_000L).ToString("D13");

    private async Task<T> Companies<T>(Func<CompanyService, Task<T>> act)
    {
        await using var db = fixture.CreateContext();
        return await act(new CompanyService(db));
    }

    private async Task Companies(Func<CompanyService, Task> act)
    {
        await using var db = fixture.CreateContext();
        await act(new CompanyService(db));
    }

    private async Task<(Guid Company, Guid Admin)> CompanyAsync(string name = "Invitation Mills Ltd")
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var dto = await Companies(s => s.RegisterAsync(admin, new RegisterCompanyRequest(name, "TL-INV", NextBin(), null, null)));
        return (dto.Id, admin);
    }

    private async Task<Guid> InviteAsync(Guid company, Guid admin, CompanyRoles roles)
    {
        var user = await TestUsers.CreateAsync(fixture);
        var member = await Companies(s => s.AddMemberAsync(admin, company, new AddMemberRequest(user, roles, null, null)));
        member.Accepted.Should().BeFalse("adding someone only invites them");
        return user;
    }

    [PostgresFact]
    public async Task AnInvitedPerson_CannotUseTheCompany_UntilTheyAccept()
    {
        var (company, admin) = await CompanyAsync();
        var invited = await InviteAsync(company, admin, CompanyRoles.Buyer | CompanyRoles.Admin);

        Func<Task> read = () => Companies(s => s.GetAsync(invited, company));
        await read.Should().ThrowAsync<KeyNotFoundException>("an invitation is not a membership");
        Func<Task> manage = () => Companies(s => s.ListMembersAsync(invited, company));
        await manage.Should().ThrowAsync<KeyNotFoundException>();
        (await Companies(s => s.ListMineAsync(invited))).Should().BeEmpty();

        var invitations = await Companies(s => s.ListInvitationsAsync(invited));
        invitations.Should().ContainSingle(i => i.CompanyId == company && i.CompanyLegalName == "Invitation Mills Ltd");

        var accepted = await Companies(s => s.AcceptInvitationAsync(invited, company));
        accepted.Company.Id.Should().Be(company);
        (await Companies(s => s.GetAsync(invited, company))).Id.Should().Be(company);
        (await Companies(s => s.ListMineAsync(invited))).Should().ContainSingle(c => c.Company.Id == company);
        (await Companies(s => s.ListInvitationsAsync(invited))).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task AnInvitedPerson_CannotBeNamedAnApprover_UntilTheyAccept()
    {
        var (company, admin) = await CompanyAsync();
        var invited = await InviteAsync(company, admin, CompanyRoles.Approver);

        await using (var db = fixture.CreateContext())
        {
            var approvals = new ApprovalService(db, TimeProvider.System);
            Func<Task> rule = () => approvals.AddRuleAsync(admin, company, new ApprovalRuleRequest("Big", 1_000, null, null, [invited]));
            (await rule.Should().ThrowAsync<ValidationException>()).WithMessage("*accepted*");
        }

        await Companies(s => s.AcceptInvitationAsync(invited, company));
        await using (var db = fixture.CreateContext())
        {
            var approvals = new ApprovalService(db, TimeProvider.System);
            (await approvals.AddRuleAsync(admin, company, new ApprovalRuleRequest("Big", 1_000, null, null, [invited]))).Id.Should().NotBeEmpty();
        }
    }

    [PostgresFact]
    public async Task AnInvitedApprover_OnAnExistingRule_IsNotAskedToDecide()
    {
        // The rule names an accepted approver; a second person is only invited with the same role and is not in the chain.
        var (company, admin) = await CompanyAsync();
        var requester = await TestUsers.CreateAsync(fixture);
        await Companies(s => s.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(requester, CompanyRoles.Requester, null, null)));
        var approver = await TestUsers.CreateAsync(fixture);
        await Companies(s => s.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(approver, CompanyRoles.Approver, null, null)));
        await using (var db = fixture.CreateContext())
            await new ApprovalService(db, TimeProvider.System).AddRuleAsync(admin, company, new ApprovalRuleRequest("Any", 1, null, null, [approver]));

        // The approver leaves; the same person is then re-added and has not accepted again.
        await using (var db = fixture.CreateContext())
        {
            var m = await db.CompanyMembers.SingleAsync(x => x.CompanyId == company && x.UserId == approver);
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE company_members SET accepted_at = NULL WHERE id = {m.Id}");
        }

        await using (var db = fixture.CreateContext())
        {
            Func<Task> submit = () => new ApprovalService(db, TimeProvider.System)
                .SubmitAsync(requester, company, new SubmitApprovalRequest("purchase_request", Guid.NewGuid(), 5_000, null, null));
            (await submit.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("NO_ELIGIBLE_APPROVER",
                "someone who has not accepted the membership is never asked to approve");
        }
    }

    [PostgresFact]
    public async Task DecliningRemovesTheInvitation()
    {
        var (company, admin) = await CompanyAsync();
        var invited = await InviteAsync(company, admin, CompanyRoles.Buyer);

        await Companies(s => s.DeclineInvitationAsync(invited, company));

        (await Companies(s => s.ListInvitationsAsync(invited))).Should().BeEmpty();
        await using var db = fixture.CreateContext();
        (await db.CompanyMembers.AnyAsync(m => m.CompanyId == company && m.UserId == invited)).Should().BeFalse();
    }

    [PostgresFact]
    public async Task NobodyCanAcceptOrDeclineSomeoneElsesInvitation_OrAMembershipTheyAlreadyHave()
    {
        var (company, admin) = await CompanyAsync();
        var invited = await InviteAsync(company, admin, CompanyRoles.Buyer);
        var stranger = await TestUsers.CreateAsync(fixture);

        Func<Task> acceptOther = () => Companies(s => s.AcceptInvitationAsync(stranger, company));
        await acceptOther.Should().ThrowAsync<KeyNotFoundException>();
        Func<Task> declineOther = () => Companies(s => s.DeclineInvitationAsync(stranger, company));
        await declineOther.Should().ThrowAsync<KeyNotFoundException>();

        // The admin's own, accepted membership is not an invitation: "declining" it must not remove them.
        Func<Task> declineOwn = () => Companies(s => s.DeclineInvitationAsync(admin, company));
        await declineOwn.Should().ThrowAsync<KeyNotFoundException>();

        await using var db = fixture.CreateContext();
        (await db.CompanyMembers.CountAsync(m => m.CompanyId == company)).Should().Be(2);
    }

    [PostgresFact]
    public async Task AnInvitedAdmin_DoesNotCountAsTheOtherAdmin()
    {
        var (company, admin) = await CompanyAsync();
        await InviteAsync(company, admin, CompanyRoles.Admin);

        var adminMember = await Companies(s => s.ListMembersAsync(admin, company));
        var self = adminMember.Single(m => m.UserId == admin);
        Func<Task> leave = () => Companies(s => s.RemoveMemberAsync(admin, company, self.Id));
        (await leave.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("LAST_ADMIN",
            "an admin who has not accepted cannot run the company, so it would be left without one");
    }

    [PostgresFact]
    public async Task InvitationsAsAdmin_CannotBlockSomeonesOwnRegistrations()
    {
        // Two strangers invite the victim as admin of their pending companies; the victim must still be able to register.
        var victim = await TestUsers.CreateAsync(fixture);
        for (int i = 0; i < CompanyService.MaxPendingRegistrationsPerUser; i++)
        {
            var (company, admin) = await CompanyAsync("Squatter " + i);
            await Companies(s => s.AddMemberAsync(admin, company, new AddMemberRequest(victim, CompanyRoles.Admin, null, null)));
        }

        var own = await Companies(s => s.RegisterAsync(victim, new RegisterCompanyRequest("Victim Ltd", "TL-V", NextBin(), null, null)));
        own.Id.Should().NotBeEmpty();
    }

    [PostgresFact]
    public async Task TheRegistrant_IsAMemberAtOnce_AndTheMembersListShowsWhoHasAccepted()
    {
        var (company, admin) = await CompanyAsync();
        var invited = await InviteAsync(company, admin, CompanyRoles.Buyer);

        var members = await Companies(s => s.ListMembersAsync(admin, company));
        members.Single(m => m.UserId == admin).Accepted.Should().BeTrue();
        members.Single(m => m.UserId == invited).Accepted.Should().BeFalse();
    }

    [PostgresFact]
    public async Task ANewRoleForSomeoneElse_IsOfferedAgain_AndNeedsANewYes()
    {
        var (company, admin) = await CompanyAsync();
        var buyer = await TestUsers.CreateAsync(fixture);
        var member = await Companies(s => s.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null)));

        // The admin turns the buyer into an approver: the buyer agreed to buy, not to approve.
        var updated = await Companies(s => s.UpdateMemberAsync(admin, company, member.Id,
            new UpdateMemberRequest(CompanyRoles.Buyer | CompanyRoles.Approver, null, null, true)));
        updated.Accepted.Should().BeFalse();
        await using (var db = fixture.CreateContext())
        {
            Func<Task> rule = () => new ApprovalService(db, TimeProvider.System)
                .AddRuleAsync(admin, company, new ApprovalRuleRequest("Big", 1_000, null, null, [buyer]));
            await rule.Should().ThrowAsync<ValidationException>();
        }
        (await Companies(s => s.ListInvitationsAsync(buyer))).Should().ContainSingle(i => i.CompanyId == company);

        await Companies(s => s.AcceptInvitationAsync(buyer, company));
        await using (var db = fixture.CreateContext())
            (await new ApprovalService(db, TimeProvider.System)
                .AddRuleAsync(admin, company, new ApprovalRuleRequest("Big", 1_000, null, null, [buyer]))).Id.Should().NotBeEmpty();

        // Taking a role away or changing a limit needs no new consent.
        var narrowed = await Companies(s => s.UpdateMemberAsync(admin, company, member.Id,
            new UpdateMemberRequest(CompanyRoles.Approver, 5_000, null, true)));
        narrowed.Accepted.Should().BeTrue();
    }

    [PostgresFact]
    public async Task AnAdminChangingTheirOwnRoles_StaysAMember()
    {
        var (company, admin) = await CompanyAsync();
        var self = (await Companies(s => s.ListMembersAsync(admin, company))).Single(m => m.UserId == admin);

        var updated = await Companies(s => s.UpdateMemberAsync(admin, company, self.Id,
            new UpdateMemberRequest(CompanyRoles.Admin | CompanyRoles.Approver, null, null, true)));

        updated.Accepted.Should().BeTrue("the company must not lose its only admin to an invitation");
        (await Companies(s => s.GetAsync(admin, company))).Id.Should().Be(company);
    }

    [PostgresFact]
    public async Task SwitchingAnInvitationOff_WithdrawsIt_AndThePersonCanBeInvitedAgain()
    {
        var (company, admin) = await CompanyAsync();
        var invited = await InviteAsync(company, admin, CompanyRoles.Buyer);
        var row = (await Companies(s => s.ListMembersAsync(admin, company))).Single(m => m.UserId == invited);

        await Companies(s => s.UpdateMemberAsync(admin, company, row.Id, new UpdateMemberRequest(CompanyRoles.Buyer, null, null, false)));

        (await Companies(s => s.ListInvitationsAsync(invited))).Should().BeEmpty();
        var again = await Companies(s => s.AddMemberAsync(admin, company, new AddMemberRequest(invited, CompanyRoles.Buyer, null, null)));
        again.Accepted.Should().BeFalse();
    }
}
