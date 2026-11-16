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
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// E7.2 over HTTP: <c>POST /api/v1/admin/orders/{id}/cancel</c> needs <c>orders.cancel</c> (Support, Finance, Admin), a fresh
/// authenticator code (action <c>order.cancel</c>) and a reason; a second call answers 200 <c>alreadyCancelled</c>.
/// </summary>
public sealed class ConfirmedOrderCancelEndpointTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "CancelStaff#2026!";
    private static long _binSeq = 3_950_000_000;

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Staff(HttpClient Client, Guid UserId);

    private async Task<Staff> StaffAsync(string role)
    {
        factory.CreateClient().Dispose();   // starts the host (its Development seeding runs once) before the account is written
        var email = $"canc.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Cancel " + role, "en");
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

    /// <summary>A company order of 50,000 on credit, charged; <paramref name="status"/> as stored.</summary>
    private async Task<(Guid Company, Guid Order)> CreditOrderAsync(string status = "Confirmed")
    {
        await using var db = Db();
        var owner = User.Create($"owner.{Guid.NewGuid():N}@example.test", null, null, "Owner");
        owner.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(owner);
        await db.SaveChangesAsync();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(owner.Id, new RegisterCompanyRequest("Cancel Api Ltd", "TL-E72-A",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, 1_000_000));
        var order = await CreditTestData.InsertOrderAsync(db, owner.Id, company, 50_000, status: status);
        if (status == "Confirmed")
        {
            var c = await db.Companies.SingleAsync(x => x.Id == company);
            db.CreditLedgerEntries.Add(new CreditLedger(c, []).Charge(order, 50_000, DateTime.UtcNow, DateTime.UtcNow).Entry);
            await db.SaveChangesAsync();
        }
        return (company, order);
    }

    private static Task<HttpResponseMessage> CancelAsync(HttpClient client, Guid order, object body, string? code)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/orders/{order}/cancel") { Content = JsonContent.Create(body) };
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return client.SendAsync(request);
    }

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

    private async Task<OrderStatus> StatusOf(Guid order)
    {
        await using var db = Db();
        return await db.Orders.Where(o => o.Id == order).Select(o => o.Status).SingleAsync();
    }

    private static readonly object Reason = new { reason = "customer called: wrong part ordered" };

    [PostgresFact]
    public async Task Cancel_ByABuyerSellerOrModerator_Is403_AndAnonymousIs401()
    {
        var (_, order) = await CreditOrderAsync();
        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Moderator })
        {
            var who = await StaffAsync(role);
            (await CancelAsync(who.Client, order, Reason, await CodeAsync(who))).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
        using var anonymous = factory.CreateClient();
        (await CancelAsync(anonymous, order, Reason, null)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await StatusOf(order)).Should().Be(OrderStatus.Confirmed);
    }

    [PostgresFact]
    public async Task Cancel_NeedsAFreshCodeAndAReason_ThenReleasesTheCredit_AndASecondCallChangesNothing()
    {
        var (company, order) = await CreditOrderAsync();
        var support = await StaffAsync(Permissions.Roles.Support);

        (await ErrorCodeAsync(await CancelAsync(support.Client, order, Reason, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await CancelAsync(support.Client, order, Reason, "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await CancelAsync(support.Client, order, new { reason = "no" }, await CodeAsync(support))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOf(order)).Should().Be(OrderStatus.Confirmed);

        var done = await DataAsync(await CancelAsync(support.Client, order, Reason, await CodeAsync(support)), HttpStatusCode.OK);
        (done.GetProperty("status").GetString(), done.GetProperty("alreadyCancelled").GetBoolean(), done.GetProperty("creditReleasedMinor").GetInt64())
            .Should().Be(("cancelled", false, 50_000));
        (await StatusOf(order)).Should().Be(OrderStatus.Cancelled);

        var finance = await StaffAsync(Permissions.Roles.Finance);
        var again = await DataAsync(await CancelAsync(finance.Client, order, Reason, await CodeAsync(finance)), HttpStatusCode.OK);
        (again.GetProperty("alreadyCancelled").GetBoolean(), again.GetProperty("creditReleasedMinor").GetInt64()).Should().Be((true, 0));

        await using var db = Db();
        (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company)).CreditUsedMinor.Should().Be(0);
        (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == support.UserId).Select(a => a.NewValuesJson).ToListAsync())
            .Should().Contain(json => json!.Contains("order.cancel") && json.Contains($"orderId={order}"));
        (await db.AuditLogs.CountAsync(a => a.Action == "order.cancelled_by_staff" && a.EntityId == order.ToString() && a.ActorId == support.UserId)).Should().Be(1);
    }

    [PostgresFact]
    public async Task Cancel_OfAPendingOrUnknownOrder_IsRefused()
    {
        var (_, pending) = await CreditOrderAsync(status: "PendingApproval");
        var admin = await StaffAsync(Permissions.Roles.Admin);

        (await ErrorCodeAsync(await CancelAsync(admin.Client, pending, Reason, await CodeAsync(admin)), HttpStatusCode.Conflict)).Should().Be("ORDER_NOT_CANCELLABLE");
        (await CancelAsync(admin.Client, Guid.NewGuid(), Reason, await CodeAsync(admin))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await StatusOf(pending)).Should().Be(OrderStatus.PendingApproval);
    }
}
