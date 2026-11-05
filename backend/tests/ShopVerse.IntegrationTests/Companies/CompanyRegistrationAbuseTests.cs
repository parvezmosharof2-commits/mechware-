using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Companies;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Companies;

/// <summary>Threat model v1, H2: a BIN cannot be squatted for good, and one account cannot stack up unverified registrations.</summary>
public class CompanyRegistrationAbuseTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static long _binSeq = 5_000_000_000;
    private static string NextBin() => (Interlocked.Increment(ref _binSeq) + 6_000_000_000_000L).ToString("D13");

    private async Task<T> Companies<T>(Func<CompanyService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new CompanyService(db));
    }

    private static RegisterCompanyRequest Registration(string bin, string name = "Meghna Textile Mills Ltd") => new(name, "TL-" + bin[^6..], bin, null, null);

    private static async Task<ConflictException> Conflict(Func<Task> act) => (await act.Should().ThrowAsync<ConflictException>()).Which;

    [PostgresFact]
    public async Task ASquattedBin_IsFreedWhenStaffRejectTheSquatter_AndTheRealOwnerCanRegisterIt()
    {
        var bin = NextBin();
        var (squatter, owner) = (await TestUsers.CreateAsync(fixture), await TestUsers.CreateAsync(fixture));
        var squatted = await Companies(s => s.RegisterAsync(squatter, Registration(bin)));

        (await Conflict(() => Companies(s => s.RegisterAsync(owner, Registration(bin))))).Code.Should().Be("COMPANY_BIN_EXISTS");

        var rejected = await Companies(s => s.AdminRejectAsync(squatted.Id, new RejectCompanyRequest("BIN belongs to another firm; trade licence does not match")));
        rejected.Status.Should().Be("rejected");
        rejected.RejectionReason.Should().Contain("another firm");

        var real = await Companies(s => s.RegisterAsync(owner, Registration(bin)));
        real.Bin.Should().Be(bin);
        (await Companies(s => s.AdminActivateAsync(real.Id))).Status.Should().Be("active");

        // The BIN is now held again: nobody else can take it.
        (await Conflict(() => Companies(s => s.RegisterAsync(squatter, Registration(bin))))).Code.Should().Be("COMPANY_BIN_EXISTS");
    }

    [PostgresFact]
    public async Task ARejectedRegistration_CannotBeActivated_NorUsed()
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var company = await Companies(s => s.RegisterAsync(admin, Registration(NextBin())));
        await Companies(s => s.AdminRejectAsync(company.Id, new RejectCompanyRequest("Documents could not be verified")));

        (await Conflict(() => Companies(s => s.AdminActivateAsync(company.Id)))).Code.Should().Be("COMPANY_REJECTED");
        (await Conflict(() => Companies(s => s.AdminSuspendAsync(company.Id)))).Code.Should().Be("COMPANY_REJECTED");
        (await Conflict(() => Companies(s => s.AddCostCentreAsync(admin, company.Id, new CostCentreRequest("CC-1", "Plant", null))))).Code
            .Should().Be("COMPANY_REJECTED");
    }

    [PostgresFact]
    public async Task OnlyAWaitingRegistration_CanBeRejected_AndOnlyWithAReason()
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var pending = await Companies(s => s.RegisterAsync(admin, Registration(NextBin())));
        await FluentActions.Awaiting(() => Companies(s => s.AdminRejectAsync(pending.Id, new RejectCompanyRequest("no"))))
            .Should().ThrowAsync<ValidationException>();

        var active = await Companies(s => s.RegisterAsync(admin, Registration(NextBin())));
        await Companies(s => s.AdminActivateAsync(active.Id));
        (await Conflict(() => Companies(s => s.AdminRejectAsync(active.Id, new RejectCompanyRequest("Too late for this"))))).Code
            .Should().Be("COMPANY_NOT_PENDING", "an active company is suspended, not rejected");
    }

    [PostgresFact]
    public async Task OneAccount_CannotHoldMoreThanTwoUnverifiedRegistrations()
    {
        var user = await TestUsers.CreateAsync(fixture);
        var first = await Companies(s => s.RegisterAsync(user, Registration(NextBin(), "First Ltd")));
        await Companies(s => s.RegisterAsync(user, Registration(NextBin(), "Second Ltd")));

        (await Conflict(() => Companies(s => s.RegisterAsync(user, Registration(NextBin(), "Third Ltd"))))).Code.Should().Be("TOO_MANY_PENDING_COMPANIES");

        // Once MECHWARE has dealt with one, another can be registered.
        await Companies(s => s.AdminActivateAsync(first.Id));
        (await Companies(s => s.RegisterAsync(user, Registration(NextBin(), "Third Ltd")))).Status.Should().Be("pending");
    }

    [PostgresFact]
    public async Task TwoFirmsRacingForAReleasedBin_OnlyOneGetsIt()
    {
        var bin = NextBin();
        var squatter = await TestUsers.CreateAsync(fixture);
        var squatted = await Companies(s => s.RegisterAsync(squatter, Registration(bin)));
        await Companies(s => s.AdminRejectAsync(squatted.Id, new RejectCompanyRequest("Not the BIN owner")));

        var racers = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => TestUsers.CreateAsync(fixture)));
        var outcomes = await Task.WhenAll(racers.Select(u => Task.Run(async () =>
        {
            try { await Companies(s => s.RegisterAsync(u, Registration(bin))); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })));

        outcomes.Count(o => o == "ok").Should().Be(1);
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "COMPANY_BIN_EXISTS");
        await using var db = fixture.CreateContext();
        (await db.Companies.AsNoTracking().CountAsync(c => c.Bin == bin)).Should().Be(2, "the rejected record stays for the audit trail");
    }

    [PostgresFact]
    public async Task ParallelRegistrations_ByOneAccount_StillStopAtTwo()
    {
        var user = await TestUsers.CreateAsync(fixture);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 6).Select(i => Task.Run(async () =>
        {
            try { await Companies(s => s.RegisterAsync(user, Registration(NextBin(), $"Parallel {i} Ltd"))); return "ok"; }
            catch (ConflictException ex) { return ex.Code; }
        })));

        outcomes.Count(o => o == "ok").Should().Be(CompanyService.MaxPendingRegistrationsPerUser);
        outcomes.Where(o => o != "ok").Should().OnlyContain(o => o == "TOO_MANY_PENDING_COMPANIES");
    }
}
