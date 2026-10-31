using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Companies;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Companies;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Companies;

public class CompanyServiceTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _binSeq = 1_000_000_000;

    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");

    private static SiteRequest Site(string name = "Gazipur Plant", bool isDefault = false) =>
        new(name, "Sabbir", "01711000000", "Plot 5, BSCIC", null, "Gazipur Sadar", "Gazipur", "Dhaka", "1700", isDefault);

    private (ShopVerseDbContext Db, CompanyService Svc) Open()
    {
        var db = fixture.CreateContext();
        return (db, new CompanyService(db));
    }

    private async Task<(Guid Company, Guid Admin)> SeedCompanyAsync()
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var (db, svc) = Open();
        await using (db)
        {
            var dto = await svc.RegisterAsync(admin, new RegisterCompanyRequest("Test Mills Ltd", "TL-1234", NextBin(), null, "Textile"));
            return (dto.Id, admin);
        }
    }

    private async Task<Guid> AddMemberAsync(Guid company, Guid admin, CompanyRoles roles)
    {
        var user = await TestUsers.CreateAsync(fixture);
        var (db, svc) = Open();
        await using (db) await svc.AddMemberAsync(admin, company, new AddMemberRequest(user, roles, null, null));
        return user;
    }

    [PostgresFact]
    public async Task AddMember_RefusesAnIdThatIsNotAnAccount_AndAnErasedAccount()
    {
        var (company, admin) = await SeedCompanyAsync();
        var (db, svc) = Open();
        await using var _ = db;

        var ghost = () => svc.AddMemberAsync(admin, company, new AddMemberRequest(Guid.NewGuid(), CompanyRoles.Buyer, null, null));
        await ghost.Should().ThrowAsync<ValidationException>();

        var erased = await TestUsers.CreateAsync(fixture);
        await using (var edit = fixture.CreateContext())
        {
            (await edit.Users.FindAsync(erased))!.AnonymizeForGdpr(DateTime.UtcNow);
            await edit.SaveChangesAsync();
        }

        var gone = () => svc.AddMemberAsync(admin, company, new AddMemberRequest(erased, CompanyRoles.Buyer, null, null));
        await gone.Should().ThrowAsync<ValidationException>();
    }

    [PostgresFact]
    public async Task TheDatabaseItselfRefusesAMembershipWithoutAnAccount()
    {
        var (company, _) = await SeedCompanyAsync();
        await using var raw = fixture.CreateContext();
        raw.CompanyMembers.Add(new CompanyMember(Guid.NewGuid(), company, Guid.NewGuid(), CompanyRoles.Buyer, null, null));
        var act = () => raw.SaveChangesAsync();
        (await act.Should().ThrowAsync<DbUpdateException>()).Which.InnerException.Should().BeOfType<Npgsql.PostgresException>()
            .Which.SqlState.Should().Be("23503");
    }

    [PostgresFact]
    public async Task Register_MakesActorFirstAdmin_AndCompanyStaysPending()
    {
        var (company, admin) = await SeedCompanyAsync();
        var (db, svc) = Open();
        await using var _ = db;

        var mine = await svc.ListMineAsync(admin);
        mine.Should().ContainSingle(m => m.Company.Id == company && m.Roles == CompanyRoles.Admin && m.Company.Status == "pending");
    }

    [PostgresFact]
    public async Task Register_DuplicateBin_IsConflict()
    {
        string bin = NextBin();
        var (db, svc) = Open();
        await using var _ = db;
        await svc.RegisterAsync(await TestUsers.CreateAsync(fixture), new RegisterCompanyRequest("First Ltd", "TL-1", bin, null, null));

        var (db2, svc2) = Open();
        await using var __ = db2;
        var secondRegistrant = await TestUsers.CreateAsync(fixture);
        var act = () => svc2.RegisterAsync(secondRegistrant, new RegisterCompanyRequest("Second Ltd", "TL-2", bin, null, null));
        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("COMPANY_BIN_EXISTS");
    }

    [PostgresFact]
    public async Task Idor_OutsiderSeesNothingOfAnotherCompany_EveryOperationAnswersNotFound()
    {
        var (company, admin) = await SeedCompanyAsync();
        var (db0, svc0) = Open();
        Guid siteId, ccId, memberId;
        await using (db0)
        {
            siteId = (await svc0.AddSiteAsync(admin, company, Site())).Id;
            ccId = (await svc0.AddCostCentreAsync(admin, company, new CostCentreRequest("PROD", "Production", 100000))).Id;
            memberId = (await svc0.ListMembersAsync(admin, company)).Single().Id;
        }

        // Another company's admin: a real, fully privileged user — but of a different tenant.
        var (otherCompany, outsider) = await SeedCompanyAsync();
        otherCompany.Should().NotBe(company);
        var (db, svc) = Open();
        await using var _ = db;

        var attempts = new Dictionary<string, Func<Task>>
        {
            ["get"] = () => svc.GetAsync(outsider, company),
            ["listSites"] = () => svc.ListSitesAsync(outsider, company),
            ["addSite"] = () => svc.AddSiteAsync(outsider, company, Site("Evil")),
            ["updateSite"] = () => svc.UpdateSiteAsync(outsider, company, siteId, Site("Evil")),
            ["removeSite"] = () => svc.RemoveSiteAsync(outsider, company, siteId),
            ["listCc"] = () => svc.ListCostCentresAsync(outsider, company),
            ["addCc"] = () => svc.AddCostCentreAsync(outsider, company, new CostCentreRequest("X", "X", null)),
            ["updateCc"] = () => svc.UpdateCostCentreAsync(outsider, company, ccId, new CostCentreRequest("X", "X", null)),
            ["removeCc"] = () => svc.RemoveCostCentreAsync(outsider, company, ccId),
            ["listMembers"] = () => svc.ListMembersAsync(outsider, company),
            ["addMember"] = () => svc.AddMemberAsync(outsider, company, new AddMemberRequest(outsider, CompanyRoles.Admin, null, null)),
            ["updateMember"] = () => svc.UpdateMemberAsync(outsider, company, memberId, new UpdateMemberRequest(CompanyRoles.Admin, null, null, true)),
            ["removeMember"] = () => svc.RemoveMemberAsync(outsider, company, memberId),
        };

        foreach (var (name, attempt) in attempts)
            (await Assert.ThrowsAsync<KeyNotFoundException>(attempt)).Message.Should().Be("Company not found.", name);

        // Nothing changed.
        var (db2, svc2) = Open();
        await using var __ = db2;
        (await svc2.ListSitesAsync(admin, company)).Should().ContainSingle(s => s.Name == "Gazipur Plant");
        (await svc2.ListMembersAsync(admin, company)).Should().ContainSingle();
    }

    [PostgresFact]
    public async Task Idor_AdminOfCompanyA_CannotTouchCompanyBChildIdsViaOwnCompanyRoute()
    {
        var (companyA, adminA) = await SeedCompanyAsync();
        var (companyB, adminB) = await SeedCompanyAsync();
        var (db0, svc0) = Open();
        Guid siteB, ccB, memberB;
        await using (db0)
        {
            siteB = (await svc0.AddSiteAsync(adminB, companyB, Site("B plant"))).Id;
            ccB = (await svc0.AddCostCentreAsync(adminB, companyB, new CostCentreRequest("B1", "B", null))).Id;
            memberB = (await svc0.ListMembersAsync(adminB, companyB)).Single().Id;
        }

        var (db, svc) = Open();
        await using var _ = db;
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.UpdateSiteAsync(adminA, companyA, siteB, Site("hijack")));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.RemoveSiteAsync(adminA, companyA, siteB));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.UpdateCostCentreAsync(adminA, companyA, ccB, new CostCentreRequest("Z", "Z", null)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.RemoveCostCentreAsync(adminA, companyA, ccB));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.UpdateMemberAsync(adminA, companyA, memberB, new UpdateMemberRequest(CompanyRoles.Buyer, null, null, true)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.RemoveMemberAsync(adminA, companyA, memberB));
    }

    [PostgresFact]
    public async Task CostCentres_ForeignIdsCannotBeAttachedToAMember()
    {
        var (companyA, adminA) = await SeedCompanyAsync();
        var (companyB, adminB) = await SeedCompanyAsync();
        var (db, svc) = Open();
        await using var _ = db;
        var ccB = await svc.AddCostCentreAsync(adminB, companyB, new CostCentreRequest("B1", "B", null));

        var someone = await TestUsers.CreateAsync(fixture);
        var act = () => svc.AddMemberAsync(adminA, companyA, new AddMemberRequest(someone, CompanyRoles.Buyer, null, [ccB.Id]));
        await act.Should().ThrowAsync<ValidationException>();
    }

    [PostgresFact]
    public async Task NonAdminMembers_CannotManage_ButCanRead_AndInactiveMembersSeeNothing()
    {
        var (company, admin) = await SeedCompanyAsync();
        var buyer = await AddMemberAsync(company, admin, CompanyRoles.Buyer);
        var (db, svc) = Open();
        await using var _ = db;

        (await svc.GetAsync(buyer, company)).Id.Should().Be(company);
        (await svc.ListSitesAsync(buyer, company)).Should().BeEmpty();
        (await Assert.ThrowsAsync<ForbiddenException>(() => svc.AddSiteAsync(buyer, company, Site()))).Code.Should().Be("COMPANY_ROLE_REQUIRED");
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.ListMembersAsync(buyer, company));
        await Assert.ThrowsAsync<ForbiddenException>(() => svc.AddMemberAsync(buyer, company, new AddMemberRequest(Guid.NewGuid(), CompanyRoles.Admin, null, null)));

        var memberId = (await svc.ListMembersAsync(admin, company)).Single(m => m.UserId == buyer).Id;
        await svc.UpdateMemberAsync(admin, company, memberId, new UpdateMemberRequest(CompanyRoles.Buyer, null, null, false));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.GetAsync(buyer, company));
    }

    [PostgresFact]
    public async Task LastAdmin_CannotBeRemovedOrDemoted_UntilAnotherAdminExists()
    {
        var (company, admin) = await SeedCompanyAsync();
        var (db, svc) = Open();
        await using var _ = db;
        var adminMember = (await svc.ListMembersAsync(admin, company)).Single();

        (await Assert.ThrowsAsync<ConflictException>(() => svc.RemoveMemberAsync(admin, company, adminMember.Id))).Code.Should().Be("LAST_ADMIN");
        (await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateMemberAsync(admin, company, adminMember.Id, new UpdateMemberRequest(CompanyRoles.Buyer, null, null, true)))).Code.Should().Be("LAST_ADMIN");
        (await Assert.ThrowsAsync<ConflictException>(() =>
            svc.UpdateMemberAsync(admin, company, adminMember.Id, new UpdateMemberRequest(CompanyRoles.Admin, null, null, false)))).Code.Should().Be("LAST_ADMIN");

        var second = await AddMemberAsync(company, admin, CompanyRoles.Admin);
        await svc.UpdateMemberAsync(admin, company, adminMember.Id, new UpdateMemberRequest(CompanyRoles.Buyer, null, null, true));
        // Now `second` is the last admin.
        var secondMember = (await svc.ListMembersAsync(second, company)).Single(m => m.UserId == second);
        await Assert.ThrowsAsync<ConflictException>(() => svc.RemoveMemberAsync(second, company, secondMember.Id));
    }

    [PostgresFact]
    public async Task TwoAdminsDemotingEachOtherConcurrently_LeaveExactlyOneAdmin()
    {
        for (int round = 0; round < 10; round++)
        {
            var (company, adminA) = await SeedCompanyAsync();
            var adminB = await AddMemberAsync(company, adminA, CompanyRoles.Admin);
            var (db0, svc0) = Open();
            Guid memberA, memberB;
            await using (db0)
            {
                var members = await svc0.ListMembersAsync(adminA, company);
                memberA = members.Single(m => m.UserId == adminA).Id;
                memberB = members.Single(m => m.UserId == adminB).Id;
            }

            using var gate = new ManualResetEventSlim(false);
            Task<bool> Demote(Guid actor, Guid target) => Task.Run(async () =>
            {
                var (db, svc) = Open();
                await using var _ = db;
                gate.Wait();
                try
                {
                    await svc.UpdateMemberAsync(actor, company, target, new UpdateMemberRequest(CompanyRoles.Buyer, null, null, true));
                    return true;
                }
                catch (Exception ex) when (ex is ConflictException or ForbiddenException or KeyNotFoundException) { return false; }
            });

            var t1 = Demote(adminA, memberB);
            var t2 = Demote(adminB, memberA);
            gate.Set();
            await Task.WhenAll(t1, t2);

            await using var check = fixture.CreateContext();
            int admins = await check.CompanyMembers.CountAsync(m => m.CompanyId == company && m.IsActive && (m.Roles & CompanyRoles.Admin) != 0);
            admins.Should().BeGreaterThanOrEqualTo(1, $"round {round}: a company must never be left without an admin");
        }
    }

    [PostgresFact]
    public async Task Sites_FirstIsDefault_ExactlyOneDefault_AndDefaultCannotBeRemovedWhileOthersExist()
    {
        var (company, admin) = await SeedCompanyAsync();
        var (db, svc) = Open();
        await using var _ = db;

        var first = await svc.AddSiteAsync(admin, company, Site("Plant 1"));
        first.IsDefaultDelivery.Should().BeTrue();
        var second = await svc.AddSiteAsync(admin, company, Site("Plant 2"));
        second.IsDefaultDelivery.Should().BeFalse();

        await svc.UpdateSiteAsync(admin, company, second.Id, Site("Plant 2", isDefault: true));
        var sites = await svc.ListSitesAsync(admin, company);
        sites.Count(s => s.IsDefaultDelivery).Should().Be(1);
        sites.Single(s => s.IsDefaultDelivery).Id.Should().Be(second.Id);

        (await Assert.ThrowsAsync<ConflictException>(() => svc.RemoveSiteAsync(admin, company, second.Id))).Code.Should().Be("DEFAULT_SITE_REQUIRED");
        await svc.RemoveSiteAsync(admin, company, first.Id);
    }

    [PostgresFact]
    public async Task CostCentres_CodeIsUniquePerCompany_AndRemovalStripsMemberReferences()
    {
        var (company, admin) = await SeedCompanyAsync();
        var (otherCompany, otherAdmin) = await SeedCompanyAsync();
        var (db, svc) = Open();
        await using var _ = db;

        var cc = await svc.AddCostCentreAsync(admin, company, new CostCentreRequest("prod", "Production", 500000));
        cc.Code.Should().Be("PROD");
        (await Assert.ThrowsAsync<ConflictException>(() => svc.AddCostCentreAsync(admin, company, new CostCentreRequest("PROD", "Dup", null)))).Code.Should().Be("COST_CENTRE_CODE_EXISTS");
        await svc.AddCostCentreAsync(otherAdmin, otherCompany, new CostCentreRequest("PROD", "Other tenant, same code is fine", null));

        var user = await TestUsers.CreateAsync(fixture);
        await svc.AddMemberAsync(admin, company, new AddMemberRequest(user, CompanyRoles.Buyer, 25000, [cc.Id]));
        await svc.RemoveCostCentreAsync(admin, company, cc.Id);
        (await svc.ListMembersAsync(admin, company)).Single(m => m.UserId == user).CostCentreIds.Should().BeEmpty();
    }

    [PostgresFact]
    public async Task SuspendedCompany_BlocksWrites_ButStillReadable()
    {
        var (company, admin) = await SeedCompanyAsync();
        var (db, svc) = Open();
        await using var _ = db;
        await svc.AdminActivateAsync(company);
        await svc.AdminSuspendAsync(company);

        (await Assert.ThrowsAsync<ConflictException>(() => svc.AddSiteAsync(admin, company, Site()))).Code.Should().Be("COMPANY_SUSPENDED");
        (await svc.GetAsync(admin, company)).Status.Should().Be("suspended");
    }

    [PostgresFact]
    public async Task Staff_CreditFlow_SetsTerms_AndRejectsInvalidOnes()
    {
        var (company, _) = await SeedCompanyAsync();
        var (db, svc) = Open();
        await using var _ = db;

        var dto = await svc.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, 50_000_000));
        dto.CreditTerms.Should().Be("net30");
        dto.CreditLimit.MinorUnits.Should().Be(50_000_000);
        dto.CreditAvailable.MinorUnits.Should().Be(50_000_000);

        await Assert.ThrowsAsync<ValidationException>(() => svc.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.None, 10)));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => svc.AdminSetCreditAsync(Guid.NewGuid(), new SetCreditRequest(CreditTerms.Net15, 10)));
    }

    [PostgresFact]
    public async Task ConcurrentCreditChanges_NeverLoseAnUpdate_AndNeverExceedTheLimit()
    {
        var (company, _) = await SeedCompanyAsync();
        await using (var seed = fixture.CreateContext())
        {
            var c = await seed.Companies.SingleAsync(x => x.Id == company);
            c.Activate();
            c.SetCredit(CreditTerms.Net30, new Money(1000, Currency.BDT));
            await seed.SaveChangesAsync();
        }

        // 30 racing orders of 100 BDT against a 1,000 BDT limit: at most 10 can win, and the stored total must match the winners.
        using var gate = new ManualResetEventSlim(false);
        var tasks = Enumerable.Range(0, 30).Select(_ => Task.Run(async () =>
        {
            await using var db = fixture.CreateContext();
            var c = await db.Companies.SingleAsync(x => x.Id == company);
            gate.Wait();
            try
            {
                c.ReserveCredit(new Money(100, Currency.BDT));
                await db.SaveChangesAsync();
                return true;
            }
            catch (Exception ex) when (ex is ConflictException or DbUpdateConcurrencyException) { return false; }
        })).ToList();
        gate.Set();
        int winners = (await Task.WhenAll(tasks)).Count(x => x);

        await using var check = fixture.CreateContext();
        var final = await check.Companies.AsNoTracking().SingleAsync(x => x.Id == company);
        winners.Should().BeInRange(1, 10);
        final.CreditUsedMinor.Should().Be(winners * 10_000L);
        final.CreditUsedMinor.Should().BeLessThanOrEqualTo(final.CreditLimitMinor);
    }
}
