using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Companies;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Api;

/// <summary>
/// Threat model v1, H3 (brief §11.1): changing credit needs a fresh authenticator code on top of the session, so a stolen
/// session alone cannot raise a company's credit. One code confirms one action.
/// </summary>
public sealed class StepUpTests : IDisposable
{
    private const string AdminEmail = "admin@mechware.com.bd";
    private const string AdminPassword = "MechAdmin#2026!";
    private const string AdminSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private static readonly TotpService Totp = new();
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private ShopVerseDbContext Db() => new(new DbContextOptionsBuilder<ShopVerseDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private async Task<HttpClient> AdminAsync()
    {
        HttpClient client = _factory.CreateClient();
        HttpResponseMessage login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = AdminEmail, password = AdminPassword, totpCode = Totp.ComputeTotpCode(AdminSecret, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        return client;
    }

    private async Task<Guid> ActiveCompanyAsync()
    {
        await using var db = Db();
        var owner = ShopVerse.Domain.Identity.User.Create($"owner.{Guid.NewGuid():N}@example.test", null, null, "Owner");
        owner.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var companies = new CompanyService(db);
        string bin = (4_000_000_000_000L + Random.Shared.Next(1, 999_999_999)).ToString("D13");
        var company = await companies.RegisterAsync(owner.Id, new RegisterCompanyRequest("Step Up Ltd", "TL-SU", bin, null, null));
        await companies.AdminActivateAsync(company.Id);
        return company.Id;
    }

    private static HttpRequestMessage SetCredit(Guid company, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Put, $"/api/v1/admin/companies/{company}/credit")
        {
            Content = JsonContent.Create(new { terms = 30, limitMinorUnits = 10_000_000 })
        };
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return request;
    }

    private async Task<long> CreditLimitAsync(Guid company)
    {
        await using var db = Db();
        return (await db.Companies.SingleAsync(c => c.Id == company)).CreditLimit.ToMinorUnits();
    }

    private async Task<(int Login, int StepUp)> AdminFailuresAsync()
    {
        await using var db = Db();
        var u = await db.Users.SingleAsync(u => u.Email == AdminEmail);
        return (u.FailedLoginAttempts, u.StepUpFailedAttempts);
    }

    private static async Task<string?> CodeOf(HttpResponseMessage r)
    {
        using JsonDocument doc = JsonDocument.Parse(await r.Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString();
    }

    [PostgresFact]
    public async Task ChangingCredit_NeedsAFreshAuthenticatorCode_AndEachCodeWorksOnce()
    {
        var company = await ActiveCompanyAsync();
        HttpClient admin = await AdminAsync();

        HttpResponseMessage none = await admin.SendAsync(SetCredit(company, null));
        none.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(none)).Should().Be("STEP_UP_REQUIRED");

        var before = await AdminFailuresAsync();
        HttpResponseMessage wrong = await admin.SendAsync(SetCredit(company, "000000"));
        wrong.StatusCode.Should().Be(HttpStatusCode.Forbidden, "403, not 401: a wrong code must not look like a lost session");
        (await CodeOf(wrong)).Should().Be("STEP_UP_FAILED");
        var after = await AdminFailuresAsync();
        after.StepUp.Should().Be(before.StepUp + 1);
        after.Login.Should().Be(before.Login, "a session holder guessing codes must not lock the owner out of signing in");
        (await CreditLimitAsync(company)).Should().Be(0);

        // Sign-in used the current time step; the next one is the fresh code.
        string next = Totp.ComputeTotpCode(AdminSecret, DateTimeOffset.UtcNow.AddSeconds(30));
        (await admin.SendAsync(SetCredit(company, next))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CreditLimitAsync(company)).Should().Be(10_000_000);
        (await AdminFailuresAsync()).StepUp.Should().Be(0, "a correct code clears the earlier misses");

        // The audit says what the code confirmed.
        await using (var db = Db())
            (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed").Select(a => a.NewValuesJson).ToListAsync())
                .Should().Contain(json => json!.Contains(company.ToString()));

        HttpResponseMessage replay = await admin.SendAsync(SetCredit(company, next));
        replay.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await CodeOf(replay)).Should().Be("STEP_UP_FAILED", "one code confirms one action");
    }

    [PostgresFact]
    public async Task SomeoneWithoutThePermission_IsRefusedBeforeAnyCodeIsChecked()
    {
        var company = await ActiveCompanyAsync();
        HttpClient buyer = _factory.CreateClient();
        HttpResponseMessage login = await buyer.PostAsJsonAsync("/api/v1/auth/login", new { identifier = "buyer@mechware.com.bd", password = "BuyerPass#2026!" });
        using (JsonDocument doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync()))
            buyer.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);

        HttpResponseMessage refused = await buyer.SendAsync(SetCredit(company, "123456"));
        refused.StatusCode.Should().Be(HttpStatusCode.Forbidden, "the permission check comes first; no code is looked at");
        await using var db = Db();
        (await db.Users.SingleAsync(u => u.Email == "buyer@mechware.com.bd")).FailedLoginAttempts.Should().Be(0);
    }

    [PostgresFact]
    public void TheSensitiveStaffActions_AllRequireStepUp()
    {
        var actions = typeof(ShopVerse.Api.Authorization.RequireStepUpAttribute).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods())
            .Select(m => (Method: m, StepUp: m.GetCustomAttributes(typeof(ShopVerse.Api.Authorization.RequireStepUpAttribute), false)
                .Cast<ShopVerse.Api.Authorization.RequireStepUpAttribute>().SingleOrDefault()))
            .Where(x => x.StepUp is not null)
            .ToDictionary(x => $"{x.Method.DeclaringType!.Name}.{x.Method.Name}", x => x.StepUp!.Action);

        actions.Should().Contain("AdminCompaniesController.SetCredit", "company.credit");
        actions.Should().Contain("AdminRefundsController.Approve", "refund.approve");
        actions.Should().Contain("AdminUserSecurityController.IssueSetupCode", "user.two_factor_setup_code");
    }

    [PostgresFact]
    public async Task FiveWrongCodes_EndTheSession_ButDoNotLockTheOwnerOut()
    {
        var company = await ActiveCompanyAsync();
        HttpClient thief = await AdminAsync();

        for (int i = 0; i < ShopVerse.Domain.Identity.User.MaxStepUpFailures - 1; i++)
            (await thief.SendAsync(SetCredit(company, "000000"))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await thief.SendAsync(SetCredit(company, "000000"))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        (await thief.GetAsync("/api/v1/users/me")).StatusCode.Should().Be(HttpStatusCode.Unauthorized, "the session that could not produce a code is over");
        (await CreditLimitAsync(company)).Should().Be(0);

        // The owner signs in again as normal (next time step: the earlier sign-in used the current one).
        HttpClient owner = _factory.CreateClient();
        HttpResponseMessage login = await owner.PostAsJsonAsync("/api/v1/auth/login", new
        {
            identifier = AdminEmail,
            password = AdminPassword,
            totpCode = Totp.ComputeTotpCode(AdminSecret, DateTimeOffset.UtcNow.AddSeconds(30))
        });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
    }
}
