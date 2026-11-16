using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
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
/// B3.4 over HTTP (threat model §১২ CS2 API, CS3, CS4, CS5, CS10): real host, real roles, real authenticator codes. Finance and
/// Admin record and verify; Support and Sellers get nothing; a verification needs a fresh code; the recorder never verifies.
/// </summary>
public sealed class CreditPaymentEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "CreditStaff#2026!";
    private static long _binSeq = 3_700_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Staff(HttpClient Client, Guid UserId);

    private async Task<Staff> StaffAsync(string role)
    {
        factory.CreateClient().Dispose(); // starts the host (its Development seeding runs once) before the account is written
        var email = $"cred.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
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

    /// <summary>An active Net-30 company with one confirmed 50,000 credit order already charged.</summary>
    private async Task<Guid> CompanyAsync(bool activate = true)
    {
        await using var db = Db();
        var owner = User.Create($"owner.{Guid.NewGuid():N}@example.test", null, null, "Owner");
        owner.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(owner.Id, new RegisterCompanyRequest("Credit Api Ltd", "TL-B34-A",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        if (!activate) return id;
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, 1_000_000));
        var order = await CreditTestData.InsertOrderAsync(db, owner.Id, id, 50_000);
        var company = await db.Companies.SingleAsync(c => c.Id == id);
        db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order, 50_000, DateTime.UtcNow, DateTime.UtcNow).Entry);
        await db.SaveChangesAsync();
        return id;
    }

    private static string Ref() => "TRX" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();

    private static object Body(long amount = 10_000, string? reference = null, string method = "beftn", string? currency = null) =>
        new { method, amountMinor = amount, receivedOn = Today, reference = reference ?? Ref(), currency };

    private static Task<HttpResponseMessage> RecordAsync(Staff who, Guid company, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/companies/{company}/credit/payments")
        {
            Content = body as HttpContent ?? JsonContent.Create(body)
        };
        request.Headers.Add("Idempotency-Key", key ?? "key-" + Guid.NewGuid().ToString("N"));
        return who.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> DecideAsync(HttpClient client, Guid payment, string action, object body, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/credit/payments/{payment}/{action}") { Content = JsonContent.Create(body) };
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> VerifyAsync(HttpClient client, Guid payment, string? code) =>
        DecideAsync(client, payment, "verify", new { clearedOn = Today }, code);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private async Task<Guid> RecordedAsync(Staff who, Guid company, long amount = 10_000) =>
        (await DataAsync(await RecordAsync(who, company, Body(amount)), HttpStatusCode.Created)).GetProperty("id").GetGuid();

    private async Task<CreditPaymentStatus> StatusOf(Guid payment)
    {
        await using var db = Db();
        return await db.CreditPayments.Where(p => p.Id == payment).Select(p => p.Status).SingleAsync();
    }

    // ---- CS3 -----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task RecordPayment_BySupportOrSeller_Is403()
    {
        var company = await CompanyAsync();
        foreach (var role in new[] { Permissions.Roles.Support, Permissions.Roles.Seller, Permissions.Roles.Moderator })
            (await RecordAsync(await StaffAsync(role), company, Body())).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        using var anonymous = factory.CreateClient();
        (await anonymous.PostAsJsonAsync($"/api/v1/admin/companies/{company}/credit/payments", Body())).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        await using var db = Db();
        (await db.CreditPayments.CountAsync(p => p.CompanyId == company)).Should().Be(0);
    }

    [PostgresFact]
    public async Task Verify_WithoutPermission_Is403()
    {
        var company = await CompanyAsync();
        var payment = await RecordedAsync(await StaffAsync(Permissions.Roles.Finance), company);
        var support = await StaffAsync(Permissions.Roles.Support);

        (await VerifyAsync(support.Client, payment, await CodeAsync(support))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await DecideAsync(support.Client, payment, "reject", new { reason = "not ours to judge" }, null)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await StatusOf(payment)).Should().Be(CreditPaymentStatus.Recorded);
    }

    [PostgresFact]
    public async Task Verify_WithoutStepUpCode_IsRefused()
    {
        var company = await CompanyAsync();
        var payment = await RecordedAsync(await StaffAsync(Permissions.Roles.Finance), company);
        var checker = await StaffAsync(Permissions.Roles.Finance);

        (await ErrorCodeAsync(await VerifyAsync(checker.Client, payment, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await VerifyAsync(checker.Client, payment, "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await StatusOf(payment)).Should().Be(CreditPaymentStatus.Recorded);

        var verified = await DataAsync(await VerifyAsync(checker.Client, payment, await CodeAsync(checker)), HttpStatusCode.OK);
        (verified.GetProperty("payment").GetProperty("status").GetString(), verified.GetProperty("creditUsedMinor").GetInt64()).Should().Be(("verified", 40_000));
        await using var db = Db();
        (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == checker.UserId).Select(a => a.NewValuesJson).ToListAsync())
            .Should().Contain(json => json!.Contains("credit.payment.verify") && json.Contains($"paymentId={payment}"));
        // A rejection releases nothing, so it needs no code.
        var other = await RecordedAsync(await StaffAsync(Permissions.Roles.Admin), company);
        (await DataAsync(await DecideAsync(checker.Client, other, "reject", new { reason = "typed twice" }, null), HttpStatusCode.OK))
            .GetProperty("status").GetString().Should().Be("rejected");
    }

    // ---- CS2 -----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Verify_ByTheRecorder_IsRefused()
    {
        var company = await CompanyAsync();
        var maker = await StaffAsync(Permissions.Roles.Finance);
        var payment = await RecordedAsync(maker, company);

        (await ErrorCodeAsync(await VerifyAsync(maker.Client, payment, await CodeAsync(maker)), HttpStatusCode.Conflict)).Should().Be("CREDIT_PAYMENT_FOUR_EYES");
        (await StatusOf(payment)).Should().Be(CreditPaymentStatus.Recorded);
        var admin = await StaffAsync(Permissions.Roles.Admin);
        await DataAsync(await VerifyAsync(admin.Client, payment, await CodeAsync(admin)), HttpStatusCode.OK);
    }

    // ---- CS4, CS5 ------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task RecordPayment_ReplayWithTheSameKey_ReturnsTheSamePayment()
    {
        var company = await CompanyAsync();
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var body = Body();
        var first = await DataAsync(await RecordAsync(finance, company, body, "pay-replay-0001"), HttpStatusCode.Created);
        var again = await DataAsync(await RecordAsync(finance, company, body, "pay-replay-0001"), HttpStatusCode.OK);

        again.GetProperty("id").GetGuid().Should().Be(first.GetProperty("id").GetGuid());
        await using var db = Db();
        (await db.CreditPayments.CountAsync(p => p.CompanyId == company)).Should().Be(1);
        // The same key from another person is their own key.
        await DataAsync(await RecordAsync(await StaffAsync(Permissions.Roles.Finance), company, Body(), "pay-replay-0001"), HttpStatusCode.Created);
    }

    [PostgresFact]
    public async Task RecordPayment_SameKeyOtherBody_Is409()
    {
        var company = await CompanyAsync();
        var finance = await StaffAsync(Permissions.Roles.Finance);
        await DataAsync(await RecordAsync(finance, company, Body(10_000), "pay-other-0001"), HttpStatusCode.Created);

        (await ErrorCodeAsync(await RecordAsync(finance, company, Body(10_001), "pay-other-0001"), HttpStatusCode.Conflict)).Should().Be("IDEMPOTENCY_KEY_REUSED");
        var noKey = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/companies/{company}/credit/payments") { Content = JsonContent.Create(Body()) };
        (await finance.Client.SendAsync(noKey)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        await using var db = Db();
        (await db.CreditPayments.CountAsync(p => p.CompanyId == company)).Should().Be(1);
    }

    [PostgresFact]
    public async Task TheSameBankReference_DifferentlyWritten_IsA409Duplicate_EvenForAnotherCompany()
    {
        var (a, b) = (await CompanyAsync(), await CompanyAsync());
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var tail = Guid.NewGuid().ToString("N")[..8];
        await DataAsync(await RecordAsync(finance, a, Body(reference: $"ab-123-{tail}")), HttpStatusCode.Created);

        (await ErrorCodeAsync(await RecordAsync(finance, b, Body(reference: $"AB 123 {tail.ToUpperInvariant()}", method: "npsb")), HttpStatusCode.Conflict))
            .Should().Be("CREDIT_PAYMENT_DUPLICATE_REFERENCE");
    }

    // ---- CS10 ----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task PaymentAmount_OutsideOneToMax_IsRefused()
    {
        var company = await CompanyAsync();
        var finance = await StaffAsync(Permissions.Roles.Finance);
        foreach (var (amount, method) in new[] { (0L, "beftn"), (-5L, "beftn"), (10_000_000_001L, "rtgs"), (20_000_001L, "cash") })
        {
            var body = method == "cash" ? (object)new { method, amountMinor = amount, receivedOn = Today } : Body(amount, method: method);
            (await RecordAsync(finance, company, body)).StatusCode.Should().Be(HttpStatusCode.BadRequest, $"{amount} by {method}");
        }
        await DataAsync(await RecordAsync(finance, company, new { method = "cash", amountMinor = 20_000_000L, receivedOn = Today }), HttpStatusCode.Created);
    }

    [PostgresFact]
    public async Task PaymentAmount_AsDecimalOrString_Is400()
    {
        var company = await CompanyAsync();
        var finance = await StaffAsync(Permissions.Roles.Finance);
        foreach (var amount in new[] { "1.5", "\"10000\"", "1e4", "10000.0", "null" })
        {
            var json = $$"""{"method":"beftn","amountMinor":{{amount}},"receivedOn":"{{Today:yyyy-MM-dd}}","reference":"{{Ref()}}"}""";
            (await RecordAsync(finance, company, new StringContent(json, Encoding.UTF8, "application/json"))).StatusCode.Should().Be(HttpStatusCode.BadRequest, amount);
        }
        await using var db = Db();
        (await db.CreditPayments.CountAsync(p => p.CompanyId == company)).Should().Be(0);
    }

    [PostgresFact]
    public async Task NonBdtCurrency_IsRefused()
    {
        var company = await CompanyAsync();
        var finance = await StaffAsync(Permissions.Roles.Finance);
        foreach (var currency in new[] { "USD", "bdt", "INR" })
            (await RecordAsync(finance, company, Body(currency: currency))).StatusCode.Should().Be(HttpStatusCode.BadRequest, currency);
        (await DataAsync(await RecordAsync(finance, company, Body(currency: "BDT")), HttpStatusCode.Created)).GetProperty("currency").GetString().Should().Be("BDT");
    }

    [PostgresFact]
    public async Task RecordPayment_ForAPendingOrUnknownCompany_Is404()
    {
        var finance = await StaffAsync(Permissions.Roles.Finance);
        foreach (var company in new[] { await CompanyAsync(activate: false), Guid.NewGuid() })
            (await ErrorCodeAsync(await RecordAsync(finance, company, Body()), HttpStatusCode.NotFound)).Should().NotBeNullOrEmpty();
    }
}
