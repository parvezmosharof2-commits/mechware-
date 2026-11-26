using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.5b over HTTP (threat model IT4; ADR-0012 §9.3, §14): real host, real roles, real authenticator codes. Its own class, so its own host and
/// login budget. Support/Finance/Admin ask (<c>invoices.credit_request</c>); only ANOTHER Finance/Admin approves or rejects, with a fresh code
/// (step-up <c>invoice.credit_note</c>); a code confirms one action; every answer is <c>private, no-store</c>.
/// </summary>
public sealed class CreditNoteEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "CreditNote#2026!";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Person(HttpClient Client, Guid UserId);

    private async Task<Person> PersonAsync(string role)
    {
        var email = $"cn.{Guid.NewGuid():N}"[..22] + "@mechware.com.bd";
        var staff = role != Permissions.Roles.Buyer;
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Credit note " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            if (staff)
            {
                user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
                user.EnableTwoFactor(DateTime.UtcNow);
            }
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", staff
            ? new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) }
            : (object)new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Person(client, id);
    }

    /// <summary>The current authenticator code, usable once more: the replay guard of this user is cleared first.</summary>
    private async Task<string> CodeAsync(Person who)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == who.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    /// <summary>A shipped company order and its Live tax invoice, issued the way ship does (counter last).</summary>
    private async Task<Invoice> InvoiceAsync()
    {
        await using var db = Db();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        var company = await InvoiceTestIssues.ActiveCompanyAsync(db);
        await using var tx = await db.Database.BeginTransactionAsync();
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, company);
        var invoice = InvoiceTestIssues.Build(order, shipment, seller,
            await new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance).TakeAsync(seller, InvoiceSeries.TaxInvoice), company);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return invoice;
    }

    private static Task<HttpResponseMessage> PostAsync(Person who, string url, object? body = null, string? key = null, string? code = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = JsonContent.Create(body ?? new { }) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> AskAsync(Person who, Invoice invoice, int quantity = 1, string? key = null) =>
        PostAsync(who, $"/api/v1/admin/invoices/{invoice.Id}/credit-notes",
            new { reason = "goods_returned", reasonText = "One unit came back damaged", lines = new[] { new { lineNo = PlcLine(invoice), quantity } } },
            key ?? "cn-" + Guid.NewGuid().ToString("N"));

    private static int PlcLine(Invoice invoice) => invoice.Lines.Single(l => l.Sku == "PLC-1200").LineNo;

    private static Task<HttpResponseMessage> DecideAsync(Person who, Guid request, string verb, string? code, object? body = null) =>
        PostAsync(who, $"/api/v1/admin/credit-note-requests/{request}/{verb}", body, code: code);

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        (r.Headers.CacheControl is { NoStore: true, Private: true }).Should().BeTrue("every answer is private, no-store (was: " + r.Headers.CacheControl + ")");
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

    [Theory]
    [InlineData(Permissions.Roles.Support, true, false)]
    [InlineData(Permissions.Roles.Finance, true, true)]
    [InlineData(Permissions.Roles.Admin, true, true)]
    [InlineData(Permissions.Roles.Moderator, false, false)]
    [InlineData(Permissions.Roles.Buyer, false, false)]
    [InlineData(Permissions.Roles.Seller, false, false)]
    public void CreditNotePermissions_PerRole(string role, bool request, bool approve)
    {
        var granted = Permissions.GetDefaultPermissionsForRole(role);
        (granted.Contains(Permissions.InvoicesCreditRequest), granted.Contains(Permissions.InvoicesCreditApprove)).Should().Be((request, approve));
    }

    [PostgresFact]
    public async Task ApproveCreditNote_WithoutStepUpOrPermission_IsRefused()
    {
        factory.CreateClient().Dispose();   // the host (and its Development seeding) first
        var invoice = await InvoiceAsync();
        var (support, financeA, financeB, admin, moderator, buyer) = (await PersonAsync(Permissions.Roles.Support), await PersonAsync(Permissions.Roles.Finance),
            await PersonAsync(Permissions.Roles.Finance), await PersonAsync(Permissions.Roles.Admin), await PersonAsync(Permissions.Roles.Moderator),
            await PersonAsync(Permissions.Roles.Buyer));

        // Who may ask and read the queue.
        (await AskAsync(moderator, invoice)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AskAsync(buyer, invoice)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await buyer.Client.GetAsync("/api/v1/admin/credit-note-requests")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await AskAsync(support, invoice, key: "x")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "an Idempotency-Key is 8-100 characters");
        var asked = await DataAsync(await AskAsync(support, invoice, key: "support-cn-0001"), HttpStatusCode.Created);
        var bySupport = asked.GetProperty("id").GetGuid();
        (asked.GetProperty("status").GetString(), asked.GetProperty("issuedNumber").ValueKind).Should().Be(("requested", JsonValueKind.Null));
        (await DataAsync(await AskAsync(support, invoice, key: "support-cn-0001"), HttpStatusCode.OK)).GetProperty("id").GetGuid().Should().Be(bySupport);
        (await ErrorCodeAsync(await AskAsync(support, invoice, quantity: 2, key: "support-cn-0001"), HttpStatusCode.Conflict)).Should().Be("IDEMPOTENCY_KEY_REUSED");
        var queue = await DataAsync(await financeA.Client.GetAsync("/api/v1/admin/credit-note-requests"), HttpStatusCode.OK);
        queue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Contain(bySupport);
        await DataAsync(await support.Client.GetAsync($"/api/v1/admin/credit-note-requests/{bySupport}"), HttpStatusCode.OK);

        // Who may approve, and only with a fresh code of their own.
        (await DecideAsync(support, bySupport, "approve", await CodeAsync(support))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "Support asks, never approves");
        (await ErrorCodeAsync(await DecideAsync(financeA, bySupport, "approve", null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await DecideAsync(admin, bySupport, "approve", "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        var own = (await DataAsync(await AskAsync(financeA, invoice), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        (await ErrorCodeAsync(await DecideAsync(financeA, own, "approve", await CodeAsync(financeA)), HttpStatusCode.Conflict)).Should().Be("CREDIT_NOTE_FOUR_EYES");
        (await ErrorCodeAsync(await DecideAsync(support, own, "withdraw", null), HttpStatusCode.Conflict)).Should().Be("CREDIT_NOTE_NOT_YOURS");

        // A code confirms one action: the code that rejected one request cannot approve another.
        var code = await CodeAsync(financeB);
        (await DataAsync(await DecideAsync(financeB, own, "reject", code, new { reason = "Duplicate of the Support request" }), HttpStatusCode.OK))
            .GetProperty("status").GetString().Should().Be("rejected");
        (await ErrorCodeAsync(await DecideAsync(financeB, bySupport, "approve", code), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");

        var approved = await DataAsync(await DecideAsync(financeB, bySupport, "approve", await CodeAsync(financeB)), HttpStatusCode.OK);
        (approved.GetProperty("request").GetProperty("status").GetString(), approved.GetProperty("refundDueMinor").GetInt64())
            .Should().Be(("approved", approved.GetProperty("totalMinor").GetInt64()));
        approved.GetProperty("number").GetString().Should().Contain("-CN-");
        await using var db = Db();
        (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == financeB.UserId).Select(a => a.NewValuesJson).ToListAsync())
            .Should().Contain(json => json!.Contains("invoice.credit_note") && json.Contains($"requestId={bySupport}"));
    }
}
