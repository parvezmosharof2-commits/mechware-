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
/// B3.6 over HTTP (threat model §১২ CS20, CS21): real host, real roles, real authenticator codes. Its own class, so its own host and
/// login budget (25 a minute per host, <c>AuthStrictPolicy</c>; <see cref="CreditPaymentEndpointsTests"/> uses ~24). Finance and
/// Admins ask; only ANOTHER Admin approves, with a fresh code; nobody who handled the source payment approves its payout.
/// </summary>
public sealed class CreditAdjustmentEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "CreditStaff#2026!";
    private static long _binSeq = 3_980_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Staff(HttpClient Client, Guid UserId);

    private async Task<Staff> StaffAsync(string role)
    {
        factory.CreateClient().Dispose(); // starts the host (its Development seeding runs once) before the account is written
        var email = $"adj.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
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
    private async Task<(Guid Company, Guid Order)> CompanyAsync()
    {
        await using var db = Db();
        var owner = User.Create($"owner.{Guid.NewGuid():N}@example.test", null, null, "Owner");
        owner.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(owner.Id, new RegisterCompanyRequest("Credit Adjust Api Ltd", "TL-B36-A",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, 1_000_000));
        var order = await CreditTestData.InsertOrderAsync(db, owner.Id, id, 50_000);
        var company = await db.Companies.SingleAsync(c => c.Id == id);
        db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order, 50_000, DateTime.UtcNow, DateTime.UtcNow).Entry);
        await db.SaveChangesAsync();
        return (id, order);
    }

    private static Task<HttpResponseMessage> PostAsync(Staff who, string url, object body, string? key = null, string? code = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> AskAsync(Staff who, Guid company, object body, string? key = null) =>
        PostAsync(who, $"/api/v1/admin/companies/{company}/credit/adjustments", body, key ?? "key-" + Guid.NewGuid().ToString("N"));

    private static Task<HttpResponseMessage> ApproveAsync(Staff who, Guid adjustment, string? code) =>
        PostAsync(who, $"/api/v1/admin/credit/adjustments/{adjustment}/approve", new { }, code: code);

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

    private async Task<long> CreditUsedAsync(Guid company)
    {
        await using var db = Db();
        return await db.Companies.Where(c => c.Id == company).Select(c => c.CreditUsedMinor).SingleAsync();
    }

    // ---- CS20 ----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task WriteOff_NeedsApprovalByAnotherAdmin_WithStepUpAndReason()
    {
        var (company, order) = await CompanyAsync();
        var finance = await StaffAsync(Permissions.Roles.Finance);
        var (adminA, adminB) = (await StaffAsync(Permissions.Roles.Admin), await StaffAsync(Permissions.Roles.Admin));
        var writeOff = new { kind = "write_off", amountMinor = 20_000, reason = "customer closed; bad debt agreed", orderId = order };

        (await AskAsync(await StaffAsync(Permissions.Roles.Support), company, writeOff)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AskAsync(finance, company, writeOff)).StatusCode.Should().Be(HttpStatusCode.Created);   // a first, separate request (own key)
        (await AskAsync(finance, company, new { kind = "write_off", amountMinor = 20_000, reason = "too short", orderId = order }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await AskAsync(finance, company, new { kind = "write_off", amountMinor = "20000", reason = "customer closed; bad debt agreed", orderId = order }))
            .StatusCode.Should().Be(HttpStatusCode.BadRequest, "an amount is a JSON integer");
        var asked = await DataAsync(await AskAsync(finance, company, writeOff, "adjust-api-0001"), HttpStatusCode.Created);
        var id = asked.GetProperty("id").GetGuid();
        (await DataAsync(await AskAsync(finance, company, writeOff, "adjust-api-0001"), HttpStatusCode.OK)).GetProperty("id").GetGuid().Should().Be(id);
        (await CreditUsedAsync(company)).Should().Be(50_000, "a request moves nothing");

        (await ApproveAsync(finance, id, await CodeAsync(finance))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "Finance asks, an Admin approves");
        (await ErrorCodeAsync(await ApproveAsync(adminB, id, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await ApproveAsync(adminB, id, "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        var own = (await DataAsync(await AskAsync(adminA, company, writeOff), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        (await ErrorCodeAsync(await ApproveAsync(adminA, own, await CodeAsync(adminA)), HttpStatusCode.Conflict)).Should().Be("CREDIT_ADJUSTMENT_FOUR_EYES");
        (await CreditUsedAsync(company)).Should().Be(50_000);

        var approved = await DataAsync(await ApproveAsync(adminB, id, await CodeAsync(adminB)), HttpStatusCode.OK);
        (approved.GetProperty("adjustment").GetProperty("status").GetString(), approved.GetProperty("creditUsedMinor").GetInt64(),
            approved.GetProperty("receivableDeltaMinor").GetInt64()).Should().Be(("approved", 30_000, -20_000));
        (await ErrorCodeAsync(await ApproveAsync(adminB, id, await CodeAsync(adminB)), HttpStatusCode.Conflict)).Should().Be("CREDIT_ADJUSTMENT_NOT_REQUESTED");
        // Anyone who may ask may also refuse or withdraw (no code: nothing moves).
        (await DataAsync(await PostAsync(finance, $"/api/v1/admin/credit/adjustments/{own}/reject", new { reason = "not agreed after all" }), HttpStatusCode.OK))
            .GetProperty("status").GetString().Should().Be("rejected");
        await using var db = Db();
        (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == adminB.UserId).Select(a => a.NewValuesJson).ToListAsync())
            .Should().Contain(json => json!.Contains("credit.adjustment.approve") && json.Contains($"adjustmentId={id}"));
    }

    // ---- CS21 ----------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Payout_NeedsFourEyesAndStepUp()
    {
        // 70,000 cash against the 50,000 order leaves 20,000 on account (cash cannot bounce, so it needs no wait). The Admin who
        // verified that cash may not approve paying it back; another Admin may, with a code and MECHWARE's own transfer reference.
        var (company, _) = await CompanyAsync();
        var (maker, checker) = (await StaffAsync(Permissions.Roles.Finance), await StaffAsync(Permissions.Roles.Admin));
        var payment = (await DataAsync(await PostAsync(maker, $"/api/v1/admin/companies/{company}/credit/payments",
            new { method = "cash", amountMinor = 70_000, receivedOn = Today }, "cash-0001"), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        await DataAsync(await PostAsync(checker, $"/api/v1/admin/credit/payments/{payment}/verify", new { clearedOn = Today }, code: await CodeAsync(checker)),
            HttpStatusCode.OK);
        object Payout(long amount, string? reference = "MW-OUT-0001") =>
            new { kind = "on_account_payout", amountMinor = amount, reason = "over-payment returned on request", paymentId = payment, payoutReference = reference };

        (await ErrorCodeAsync(await AskAsync(maker, company, Payout(20_001)), HttpStatusCode.Conflict)).Should().Be("CREDIT_ON_ACCOUNT_INSUFFICIENT");
        (await AskAsync(maker, company, Payout(20_000, null))).StatusCode.Should().Be(HttpStatusCode.BadRequest, "MECHWARE's bank reference is required");
        var id = (await DataAsync(await AskAsync(maker, company, Payout(20_000)), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        (await ErrorCodeAsync(await ApproveAsync(checker, id, await CodeAsync(checker)), HttpStatusCode.Conflict)).Should().Be("CREDIT_ADJUSTMENT_FOUR_EYES");
        var admin = await StaffAsync(Permissions.Roles.Admin);
        (await ErrorCodeAsync(await ApproveAsync(admin, id, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        var paidOut = await DataAsync(await ApproveAsync(admin, id, await CodeAsync(admin)), HttpStatusCode.OK);
        (paidOut.GetProperty("onAccountDeltaMinor").GetInt64(), paidOut.GetProperty("creditOnAccountMinor").GetInt64(),
            paidOut.GetProperty("adjustment").GetProperty("payoutReference").GetString()).Should().Be((-20_000, 0, "MWOUT0001"));
    }
}
