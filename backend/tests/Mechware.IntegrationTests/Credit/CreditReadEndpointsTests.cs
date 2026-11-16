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
/// B3.8 over HTTP (threat model §১২ CS27): real host, real roles, real authenticator codes. Its own class, so its own host and login
/// budget (25 a minute per host). Only Finance and Admin read the credit books (Support, anonymous: refused); the 201 <c>Location</c>
/// headers of the payment and adjustment requests now lead to a GET; the all-company CSV needs a fresh code; every CSV is audited and
/// rate-limited per person.
/// </summary>
public sealed class CreditReadEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "CreditStaff#2026!";
    private static long _binSeq = 3_960_000_000;

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Staff(HttpClient Client, Guid UserId);

    private async Task<Staff> StaffAsync(string role)
    {
        factory.CreateClient().Dispose(); // starts the host (its Development seeding runs once) before the account is written
        var email = $"reads.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
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

    private async Task<string> CodeAsync(Staff staff)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == staff.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    /// <summary>An active Net-30 company with one order charged 40 days ago (10 days past due).</summary>
    private async Task<(Guid Company, Guid Order)> CompanyWithDebtAsync()
    {
        await using var db = Db();
        var owner = User.Create($"owner.{Guid.NewGuid():N}@example.test", null, null, "Owner");
        owner.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(owner.Id, new RegisterCompanyRequest("Credit Reads Api Ltd", "TL-B38-A",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        var order = await CreditTestData.InsertOrderAsync(db, owner.Id, id, 50_000);
        var company = await db.Companies.SingleAsync(c => c.Id == id);
        var at = DateTime.UtcNow.AddDays(-40);
        db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order, 50_000, at, at).Entry);
        await db.SaveChangesAsync();
        return (id, order);
    }

    private static Task<HttpResponseMessage> GetAsync(Staff who, string url, string? code = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.Client.SendAsync(request);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(HttpStatusCode.OK, text);
        return JsonDocument.Parse(text).RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static HttpRequestMessage Post(string url, object body, string key)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        request.Headers.Add("Idempotency-Key", key);
        return request;
    }

    [PostgresFact]
    public async Task OnlyFinanceAndAdmin_ReadTheCreditBooks_AndTheLocationHeadersLeadToAGet()
    {
        var (company, order) = await CompanyWithDebtAsync();
        var (support, finance, admin) = (await StaffAsync(Permissions.Roles.Support), await StaffAsync(Permissions.Roles.Finance), await StaffAsync(Permissions.Roles.Admin));
        var today = CreditCalendar.DhakaDate(DateTime.UtcNow).ToString("yyyy-MM-dd");

        // One request that creates a payment and one that asks for an adjustment: their Location headers are the GET URLs.
        var recorded = await finance.Client.SendAsync(Post($"/api/v1/admin/companies/{company}/credit/payments",
            new { method = "rtgs", amountMinor = 20_000, receivedOn = today, reference = "RTGS" + Guid.NewGuid().ToString("N")[..10] }, "key-" + Guid.NewGuid().ToString("N")));
        recorded.StatusCode.Should().Be(HttpStatusCode.Created, await recorded.Content.ReadAsStringAsync());
        var paymentUrl = recorded.Headers.Location!.ToString();
        var asked = await finance.Client.SendAsync(Post($"/api/v1/admin/companies/{company}/credit/adjustments",
            new { kind = "write_off", amountMinor = 1_000, reason = "customer closed; bad debt agreed", orderId = order }, "key-" + Guid.NewGuid().ToString("N")));
        asked.StatusCode.Should().Be(HttpStatusCode.Created, await asked.Content.ReadAsStringAsync());
        var adjustmentUrl = asked.Headers.Location!.ToString();

        string[] urls =
        [
            paymentUrl, adjustmentUrl, "/api/v1/admin/credit/payments", "/api/v1/admin/credit/adjustments", "/api/v1/admin/credit/aging",
            $"/api/v1/admin/companies/{company}/credit/statement"
        ];
        foreach (var url in urls)
        {
            (await GetAsync(support, url)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"Support must not read {url}");
            (await factory.CreateClient().GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"anonymous must not read {url}");
            foreach (var who in new[] { finance, admin }) (await GetAsync(who, url)).StatusCode.Should().Be(HttpStatusCode.OK, $"{url} for finance and admin");
        }

        (await GetAsync(support, "/api/v1/admin/credit/aging.csv", await CodeAsync(support))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await GetAsync(support, $"/api/v1/admin/companies/{company}/credit/statement.csv")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await factory.CreateClient().GetAsync($"/api/v1/admin/companies/{company}/credit/statement.csv")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.CreateClient().GetAsync("/api/v1/admin/credit/aging.csv")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);

        var payment = await DataAsync(await GetAsync(admin, paymentUrl));
        (payment.GetProperty("payment").GetProperty("status").GetString(), payment.GetProperty("payment").GetProperty("companyId").GetGuid()).Should().Be(("recorded", company));
        (await DataAsync(await GetAsync(admin, adjustmentUrl))).GetProperty("adjustment").GetProperty("status").GetString().Should().Be("requested");
        var queue = await DataAsync(await GetAsync(finance, $"/api/v1/admin/credit/payments?companyId={company}"));
        queue.GetProperty("items").GetArrayLength().Should().Be(1);
        (await GetAsync(finance, "/api/v1/admin/credit/payments?status=nonsense")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(finance, $"/api/v1/admin/credit/payments/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync(finance, $"/api/v1/admin/companies/{Guid.NewGuid()}/credit/statement")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await GetAsync(finance, $"/api/v1/admin/companies/{company}/credit/statement?from=2026-12-02&to=2026-12-01")).StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var aging = await DataAsync(await GetAsync(finance, $"/api/v1/admin/credit/aging?companyId={company}&overdueOnly=true"));
        var row = aging.GetProperty("companies").GetProperty("items")[0];
        (row.GetProperty("buckets").GetProperty("days1To30").GetInt64(), aging.GetProperty("overdueMinor").GetInt64()).Should().Be((50_000, 50_000));
        var statement = await DataAsync(await GetAsync(finance, $"/api/v1/admin/companies/{company}/credit/statement"));
        statement.GetProperty("currentReceivableMinor").GetInt64().Should().Be(50_000);

        // CSV: the file headers, then the code for the all-company file, and the audit rows.
        var csv = await GetAsync(finance, $"/api/v1/admin/companies/{company}/credit/statement.csv");
        (csv.Content.Headers.ContentType!.ToString(), csv.Headers.CacheControl!.NoStore).Should().Be(("text/csv; charset=utf-8", true));
        csv.Content.Headers.ContentDisposition!.FileName.Should().Be($"credit-statement-{company:N}.csv");
        (await csv.Content.ReadAsByteArrayAsync()).Take(3).Should().Equal(0xEF, 0xBB, 0xBF);

        (await ErrorCodeAsync(await GetAsync(finance, "/api/v1/admin/credit/aging.csv"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await GetAsync(finance, "/api/v1/admin/credit/aging.csv", "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        var all = await GetAsync(finance, "/api/v1/admin/credit/aging.csv", await CodeAsync(finance));
        all.StatusCode.Should().Be(HttpStatusCode.OK, await all.Content.ReadAsStringAsync());
        (await all.Content.ReadAsStringAsync()).Should().Contain("Credit Reads Api Ltd");

        await using var db = Db();
        (await db.AuditLogs.Where(a => a.ActorId == finance.UserId && a.Action.EndsWith("_exported")).Select(a => a.Action).OrderBy(a => a).ToListAsync())
            .Should().Equal("credit.aging_exported", "credit.statement_exported");
        (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == finance.UserId).Select(a => a.NewValuesJson).ToListAsync())
            .Should().Contain(json => json!.Contains("credit.export_all"));
        (await db.AuditLogs.CountAsync(a => a.ActorId == admin.UserId && a.Action.EndsWith("_exported"))).Should().Be(0, "an Admin who only read JSON wrote no export audit row");
    }

    [PostgresFact]
    public async Task CsvExports_AreRateLimited_PerPerson()
    {
        var (company, _) = await CompanyWithDebtAsync();
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var url = $"/api/v1/admin/companies/{company}/credit/statement.csv";
        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 8; i++) statuses.Add((await GetAsync(finance, url)).StatusCode);
        statuses.Take(6).Should().OnlyContain(s => s == HttpStatusCode.OK);
        statuses.Skip(6).Should().OnlyContain(s => s == HttpStatusCode.TooManyRequests);
        (await GetAsync(finance, $"/api/v1/admin/companies/{company}/credit/statement")).StatusCode.Should().Be(HttpStatusCode.OK, "only the exports have the small budget");
    }
}
