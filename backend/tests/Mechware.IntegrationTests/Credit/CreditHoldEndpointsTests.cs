using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Credit;

/// <summary>
/// B3.7 over HTTP (threat model §১২ CS25): real host, real roles, real authenticator codes. Its own class, so its own host and login
/// budget (25 a minute per host). Only an Admin lifts the overdue hold, with a fresh code and a reason, for at most 7 days; the lift
/// lets credit orders through for its days only.
/// </summary>
public sealed class CreditHoldEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "CreditStaff#2026!";
    private static long _binSeq = 3_995_000_000;

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Staff(HttpClient Client, Guid UserId);

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(DateTime.SpecifyKind(utc, DateTimeKind.Utc));
    }

    private async Task<Staff> StaffAsync(string role)
    {
        factory.CreateClient().Dispose(); // starts the host (its Development seeding runs once) before the account is written
        var email = $"hold.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Credit " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Staff(client, id);
    }

    /// <summary>The current authenticator code, usable once more: the replay guard of this user is cleared first.</summary>
    private async Task<string> CodeAsync(Staff staff)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == staff.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    /// <summary>An active Net-30 company with a buyer and one credit order charged 50 days ago: 20 days past due, on hold.</summary>
    private async Task<(Guid Company, Guid Buyer)> OverdueCompanyAsync()
    {
        await using var db = Db();
        var owner = User.Create($"owner.{Guid.NewGuid():N}@example.test", null, null, "Owner");
        var buyer = User.Create($"buyer.{Guid.NewGuid():N}@example.test", null, null, "Buyer");
        foreach (var u in new[] { owner, buyer })
        {
            u.VerifyEmail(DateTime.UtcNow);
            db.Users.Add(u);
        }
        db.Addresses.Add(TestUsers.DefaultAddress(buyer.Id));
        await db.SaveChangesAsync();
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(owner.Id, new RegisterCompanyRequest("Credit Hold Api Ltd", "TL-B37-A",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        await companies.AddAcceptedMemberAsync(owner.Id, id, new AddMemberRequest(buyer.Id, CompanyRoles.Buyer, null, null));
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        var order = await CreditTestData.InsertOrderAsync(db, owner.Id, id, 50_000);
        var company = await db.Companies.SingleAsync(c => c.Id == id);
        var at = DateTime.UtcNow.AddDays(-50);
        db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order, 50_000, at, at).Entry);
        await db.SaveChangesAsync();
        return (id, buyer.Id);
    }

    private static Task<HttpResponseMessage> PutAsync(Staff who, Guid company, object body, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/companies/{company}/credit/hold-override") { Content = JsonContent.Create(body) };
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.Client.SendAsync(request);
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private async Task<string> PlaceAsync(Guid buyer, Guid company, Guid variant, DateTime now)
    {
        await using var db = Db();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), new FixedClock(now),
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db));
        try { return (await orders.PlaceAsync(new PlaceOrderRequest(buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(variant, 1)], null, company, PayOnCredit: true))).Status; }
        catch (ConflictException ex) { return ex.Code; }
    }

    [PostgresFact]
    public async Task HoldOverride_NeedsAdminStepUpAndReason_AndExpires()
    {
        var (company, buyer) = await OverdueCompanyAsync();
        Guid variant;
        await using (var db = Db()) variant = (await CatalogTestData.SeedVariantAsync(db, 10, priceMinor: 100_000)).VariantId;
        var start = DateTime.UtcNow;
        (await PlaceAsync(buyer, company, variant, start)).Should().Be("CREDIT_HOLD_OVERDUE");
        var lift = new { days = 3, reason = "customer promised a cheque on Thursday" };

        var (support, finance, admin) = (await StaffAsync(Permissions.Roles.Support), await StaffAsync(Permissions.Roles.Finance), await StaffAsync(Permissions.Roles.Admin));
        (await PutAsync(support, company, lift, await CodeAsync(support))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await PutAsync(finance, company, lift, await CodeAsync(finance))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "only an Admin lifts the hold");
        (await ErrorCodeAsync(await PutAsync(admin, company, lift, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await PutAsync(admin, company, lift, "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await PutAsync(admin, company, new { days = 8, reason = lift.reason }, await CodeAsync(admin))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "7 days at most");
        (await PutAsync(admin, company, new { days = 3, reason = "ok" }, await CodeAsync(admin))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "a reason is required");
        (await PutAsync(admin, company, new { days = "3", reason = lift.reason }, await CodeAsync(admin))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "days is a JSON integer");
        (await PutAsync(admin, company, new { reason = lift.reason }, await CodeAsync(admin))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "days is required");
        (await PutAsync(admin, Guid.NewGuid(), lift, await CodeAsync(admin))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await PlaceAsync(buyer, company, variant, start)).Should().Be("CREDIT_HOLD_OVERDUE", "nothing above lifted the hold");

        var response = await PutAsync(admin, company, lift, await CodeAsync(admin));
        var text = await response.Content.ReadAsStringAsync();
        response.StatusCode.Should().Be(HttpStatusCode.OK, text);
        using (var doc = JsonDocument.Parse(text))
        {
            var data = doc.RootElement.GetProperty("data");
            (data.GetProperty("onHold").GetBoolean(), data.GetProperty("overdueSince").GetString())
                .Should().Be((false, CreditCalendar.DhakaDate(DateTime.UtcNow).AddDays(-20).ToString("yyyy-MM-dd")));
            data.GetProperty("overrideUntilUtc").GetDateTime().ToUniversalTime().Should().BeCloseTo(DateTime.UtcNow.AddDays(3), TimeSpan.FromMinutes(1));
        }

        (await PlaceAsync(buyer, company, variant, DateTime.UtcNow.AddHours(1))).Should().Be("confirmed");
        (await PlaceAsync(buyer, company, variant, DateTime.UtcNow.AddDays(3).AddMinutes(1))).Should().Be("CREDIT_HOLD_OVERDUE", "the lift has expired");

        await using (var db = Db())
        {
            (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == admin.UserId).Select(a => a.NewValuesJson).ToListAsync())
                .Should().Contain(json => json!.Contains("credit.hold.override") && json.Contains($"companyId={company}"));
            var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == "credit.hold_overridden" && a.EntityId == company.ToString());
            // (The test server has no remote address; the IP column is covered by the service test.)
            (audit.ActorId, audit.CorrelationId is not null).Should().Be(((Guid?)admin.UserId, true));
            audit.NewValuesJson.Should().Contain("customer promised a cheque on Thursday");
        }
    }
}
