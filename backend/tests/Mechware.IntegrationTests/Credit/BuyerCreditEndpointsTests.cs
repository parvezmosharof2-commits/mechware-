using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// B3.9 over HTTP (threat model CS22, CS23, CS24): the real host, real sign-ins, real roles. Its own class, so its own host and login budget
/// (25 a minute per host; this class signs in 7 times). The matrix is the point: another company's Admin, a person with no company, platform
/// staff (Finance with a real authenticator code), a Requester and an Approver of the SAME company and an anonymous caller all fail, and every
/// 404 is the same body as the 404 of a company that does not exist. The same rule narrows <c>GET /companies/{id}</c> and <c>OrderDto.credit</c>.
/// </summary>
public sealed class BuyerCreditEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Password = "BuyerCredit#2026!";
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private static long _binSeq = 3_990_000_000;

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Person(Guid Id, HttpClient Client);

    private async Task<Guid> UserRowAsync(string role, string email, string? hash, bool twoFactor = false)
    {
        await using var db = Db();
        var user = User.Create(email, null, hash, "Buyer Credit " + role, "en");
        user.VerifyEmail(DateTime.UtcNow);
        user.AssignRole(Guid.NewGuid(), role);
        if (twoFactor)
        {
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
        }
        db.Users.Add(user);
        db.Addresses.Add(TestUsers.DefaultAddress(user.Id));
        await db.SaveChangesAsync();
        return user.Id;
    }

    private async Task<HttpClient> SignInAsync(string email, bool twoFactor)
    {
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", twoFactor
            ? new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) }
            : (object)new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }

    private async Task<Person> PersonAsync(string role = Permissions.Roles.Buyer)
    {
        factory.CreateClient().Dispose(); // starts the host (its Development seeding runs once) before the account is written
        var email = $"bc.{Guid.NewGuid():N}"[..22] + "@example.test";
        var id = await UserRowAsync(role, email, IdentityTestKit.Hasher.HashPassword(Password), twoFactor: role is Permissions.Roles.Finance or Permissions.Roles.Admin);
        return new Person(id, await SignInAsync(email, role is Permissions.Roles.Finance or Permissions.Roles.Admin));
    }

    private async Task<Guid> CompanyAsync(Guid admin, string name, params (Guid User, CompanyRoles Roles)[] members)
    {
        await using var db = Db();
        var companies = new CompanyService(db);
        var bin = (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13");
        var id = (await companies.RegisterAsync(admin, new RegisterCompanyRequest(name, "TL-B39-" + bin[^5..], bin, null, null))).Id;
        foreach (var (user, roles) in members) await companies.AddAcceptedMemberAsync(admin, id, new AddMemberRequest(user, roles, null, null));
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        return id;
    }

    /// <summary>An order of <paramref name="placedBy"/> charged <paramref name="daysAgo"/> days ago (Net-30).</summary>
    private async Task<Guid> ChargedOrderAsync(Guid company, Guid placedBy, long amount, int daysAgo)
    {
        await using var db = Db();
        var order = await CreditTestData.InsertOrderAsync(db, placedBy, company, amount);
        var entity = await db.Companies.SingleAsync(c => c.Id == company);
        var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == company).ToListAsync();
        var at = DateTime.UtcNow.AddDays(-daysAgo);
        db.CreditLedgerEntries.Add(new CreditLedger(entity, lines).Charge(order, amount, at, at).Entry);
        await db.SaveChangesAsync();
        return order;
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    /// <summary>The part of a 404 that must not differ: code and message (the trace id of course does).</summary>
    private static async Task<string> NotFoundShapeAsync(HttpResponseMessage r, string because)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.NotFound, because + ": " + text);
        using var doc = JsonDocument.Parse(text);
        var error = doc.RootElement.GetProperty("error");
        return error.GetProperty("code").GetString() + "|" + error.GetProperty("message").GetString();
    }

    private static string[] CreditUrls(Guid company) =>
        [$"/api/v1/companies/{company}/credit", $"/api/v1/companies/{company}/credit/open-items", $"/api/v1/companies/{company}/credit/statement"];

    [PostgresFact]
    public async Task Idor_CreditOfAnotherCompany_Is404_ForEveryoneWhoIsNotAllowed_WithOneBody()
    {
        var (ownerA, buyerA, requesterA, approverA) = (await PersonAsync(), await PersonAsync(), await PersonAsync(), await PersonAsync());
        var (ownerB, nobody, finance, admin) = (await PersonAsync(), await PersonAsync(), await PersonAsync(Permissions.Roles.Finance), await PersonAsync(Permissions.Roles.Admin));
        var companyA = await CompanyAsync(ownerA.Id, "Buyer Credit A Ltd", (buyerA.Id, CompanyRoles.Buyer), (requesterA.Id, CompanyRoles.Requester), (approverA.Id, CompanyRoles.Approver));
        var companyB = await CompanyAsync(ownerB.Id, "Buyer Credit B Ltd");
        await ChargedOrderAsync(companyA, buyerA.Id, 25_000, 5);
        await ChargedOrderAsync(companyB, ownerB.Id, 88_000, 5);

        // Allowed: the Admin and the Buyer of the company, each one only their own company.
        foreach (var url in CreditUrls(companyA))
            foreach (var who in new[] { ownerA, buyerA })
            {
                var ok = await who.Client.GetAsync(url);
                (await DataAsync(ok)).ValueKind.Should().Be(JsonValueKind.Object);
                ok.Headers.CacheControl!.NoStore.Should().BeTrue("a private answer is never kept: " + url);
                ok.Headers.CacheControl.Private.Should().BeTrue();
                ok.Headers.Vary.Should().Contain("Authorization");
            }
        var own = await DataAsync(await ownerB.Client.GetAsync(CreditUrls(companyB)[0]));
        own.GetProperty("usedMinor").GetInt64().Should().Be(88_000);

        // Not allowed: every one of them gets the same 404 as for a company id that does not exist.
        var unknown = Guid.NewGuid();
        var denied = new (Person Who, string Label)[]
        {
            (ownerB, "the Admin of ANOTHER company"), (nobody, "a person with no company"), (requesterA, "a Requester of the company"),
            (approverA, "an Approver of the company"), (finance, "platform Finance staff"), (admin, "platform Admin staff")
        };
        for (var i = 0; i < 3; i++)
        {
            var baseline = await NotFoundShapeAsync(await buyerA.Client.GetAsync(CreditUrls(unknown)[i]), "unknown company");
            foreach (var (who, label) in denied)
            {
                var response = await who.Client.GetAsync(CreditUrls(companyA)[i]);
                (await NotFoundShapeAsync(response, label)).Should().Be(baseline, label + " gets the unknown-company answer");
                response.Headers.CacheControl?.NoStore.Should().BeTrue("also a refusal is not kept: " + label);
                response.Headers.Vary.Should().Contain("Authorization");
            }
        }

        // Anonymous: 401, never the data.
        using var anonymous = factory.CreateClient();
        foreach (var url in CreditUrls(companyA)) (await anonymous.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        // The numbers of company B never appear in anything company A's people can read.
        var all = string.Concat(await Task.WhenAll(CreditUrls(companyA).Select(async u => await (await buyerA.Client.GetAsync(u)).Content.ReadAsStringAsync())));
        all.Should().NotContain("88000");
    }

    [PostgresFact]
    public async Task TheReads_Work_PagedAndFiltered_AndRefuseBadInput()
    {
        var (owner, buyer) = (await PersonAsync(), await PersonAsync());
        var company = await CompanyAsync(owner.Id, "Buyer Credit Reads Ltd", (buyer.Id, CompanyRoles.Buyer));
        await ChargedOrderAsync(company, buyer.Id, 25_000, 40);   // 10 days past due
        await ChargedOrderAsync(company, buyer.Id, 5_000, 0);
        var today = CreditCalendar.DhakaDate(DateTime.UtcNow);

        var credit = await DataAsync(await buyer.Client.GetAsync($"/api/v1/companies/{company}/credit"));
        credit.GetProperty("usedMinor").GetInt64().Should().Be(30_000);
        credit.GetProperty("overdueMinor").GetInt64().Should().Be(25_000);
        credit.GetProperty("onHold").GetBoolean().Should().BeFalse();
        credit.GetProperty("aging").GetProperty("days1To30").GetInt64().Should().Be(25_000);

        var items = await DataAsync(await buyer.Client.GetAsync($"/api/v1/companies/{company}/credit/open-items?page=1&pageSize=1"));
        items.GetProperty("totalCount").GetInt32().Should().Be(2);
        items.GetProperty("items").EnumerateArray().Single().GetProperty("status").GetString().Should().Be("overdue");

        var statement = await DataAsync(await buyer.Client.GetAsync($"/api/v1/companies/{company}/credit/statement?from={today.AddDays(-50):yyyy-MM-dd}&to={today.AddDays(-35):yyyy-MM-dd}"));
        statement.GetProperty("lines").GetProperty("totalCount").GetInt32().Should().Be(1);
        (await buyer.Client.GetAsync($"/api/v1/companies/{company}/credit/statement?from={today:yyyy-MM-dd}&to={today.AddDays(-1):yyyy-MM-dd}")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await buyer.Client.GetAsync($"/api/v1/companies/{company}/credit/statement?from=not-a-date")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await buyer.Client.GetAsync($"/api/v1/companies/not-a-guid/credit")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task TheCompanyView_NarrowsTheCreditFields_ToAdminsAndBuyers()
    {
        var (owner, buyer, requester) = (await PersonAsync(), await PersonAsync(), await PersonAsync());
        var company = await CompanyAsync(owner.Id, "Buyer Credit View Ltd", (buyer.Id, CompanyRoles.Buyer), (requester.Id, CompanyRoles.Requester));

        foreach (var who in new[] { owner, buyer })
        {
            var full = await DataAsync(await who.Client.GetAsync($"/api/v1/companies/{company}"));
            full.GetProperty("creditLimit").GetProperty("minorUnits").GetInt64().Should().Be(100_000_000);
            full.GetProperty("creditUsed").ValueKind.Should().Be(JsonValueKind.Object);
            full.GetProperty("creditAvailable").ValueKind.Should().Be(JsonValueKind.Object);
        }

        var narrow = await DataAsync(await requester.Client.GetAsync($"/api/v1/companies/{company}"));
        narrow.GetProperty("creditTerms").GetString().Should().Be("net30");
        foreach (var field in new[] { "creditLimit", "creditUsed", "creditAvailable" }) narrow.GetProperty(field).ValueKind.Should().Be(JsonValueKind.Null, field);

        var mine = await DataAsync(await requester.Client.GetAsync("/api/v1/companies"));
        mine.EnumerateArray().Single().GetProperty("company").GetProperty("creditLimit").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [PostgresFact]
    public async Task OrderCredit_ShowsTheLedgerStanding_OnlyToAPersonWhoMayStillSeeTheCredit()
    {
        var (owner, buyer, requester, leaver) = (await PersonAsync(), await PersonAsync(), await PersonAsync(), await PersonAsync());
        var company = await CompanyAsync(owner.Id, "Buyer Credit Orders Ltd", (buyer.Id, CompanyRoles.Buyer), (requester.Id, CompanyRoles.Requester), (leaver.Id, CompanyRoles.Buyer));
        var overdue = await ChargedOrderAsync(company, buyer.Id, 25_000, 40);
        var fresh = await ChargedOrderAsync(company, buyer.Id, 9_000, 0);
        var requesters = await ChargedOrderAsync(company, requester.Id, 7_000, 1);
        var leavers = await ChargedOrderAsync(company, leaver.Id, 6_000, 1);
        await using (var db = Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE company_members SET is_active = false WHERE company_id = {company} AND user_id = {leaver.Id}");
        await using (var db = Db())
        {
            // An ordinary order (not on credit) of the same buyer, and a credit order that has no charge yet (waiting for an approval).
            var plain = await CreditTestData.InsertOrderAsync(db, buyer.Id, company, 1_000, onCredit: false);
            var waiting = await CreditTestData.InsertOrderAsync(db, buyer.Id, company, 2_000, status: "PendingApproval");
            (await (await buyer.Client.GetAsync($"/api/v1/orders/{plain}")).Content.ReadAsStringAsync()).Should().Contain("\"credit\":null");
            (await (await buyer.Client.GetAsync($"/api/v1/orders/{waiting}")).Content.ReadAsStringAsync()).Should().Contain("\"credit\":null");
        }

        var late = (await DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/{overdue}"))).GetProperty("credit");
        late.GetProperty("status").GetString().Should().Be("overdue");
        late.GetProperty("outstandingMinor").GetInt64().Should().Be(25_000);
        late.GetProperty("dueOn").GetString().Should().Be(CreditCalendar.DhakaDate(DateTime.UtcNow.AddDays(-10)).ToString("yyyy-MM-dd"));
        (await DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/{fresh}"))).GetProperty("credit").GetProperty("status").GetString().Should().Be("open");

        // A Requester placed this order but may not see the company's credit; a person who left the company no longer may.
        (await DataAsync(await requester.Client.GetAsync($"/api/v1/orders/{requesters}"))).GetProperty("credit").ValueKind.Should().Be(JsonValueKind.Null);
        (await DataAsync(await leaver.Client.GetAsync($"/api/v1/orders/{leavers}"))).GetProperty("credit").ValueKind.Should().Be(JsonValueKind.Null);

        // Someone else's order is still a 404 (the existing owner rule), with or without credit rights.
        (await owner.Client.GetAsync($"/api/v1/orders/{overdue}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task ASuspendedCompany_StillReadsItsCreditOverHttp()
    {
        var owner = await PersonAsync();
        var company = await CompanyAsync(owner.Id, "Buyer Credit Suspended Ltd");
        await ChargedOrderAsync(company, owner.Id, 12_000, 3);
        await using (var db = Db()) await new CompanyService(db).AdminSuspendAsync(company);

        foreach (var url in CreditUrls(company)) (await owner.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.OK, url);
    }
}
