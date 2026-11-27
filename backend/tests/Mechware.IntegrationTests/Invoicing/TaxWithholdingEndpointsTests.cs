using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.6b over HTTP (threat model IT14; ADR-0012 §11, §14): real host, roles and authenticator codes. Finance/Admin record
/// (<c>credit.record_payment</c>); ANOTHER Finance/Admin verifies with a fresh code (<c>credit.verify_payment</c>, step-up <c>invoice.withholding</c>);
/// Support reads (<c>invoices.view</c>); only Admin sets the company hint (<c>companies.manage</c>, step-up). Every answer is <c>private, no-store</c>.
/// </summary>
public sealed class TaxWithholdingEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "Withholding#2026!";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Person(HttpClient Client, Guid UserId);

    private async Task<Person> PersonAsync(string role)
    {
        var email = $"wh.{Guid.NewGuid():N}"[..22] + "@mechware.com.bd";
        var staff = role != Permissions.Roles.Buyer;
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Withholding " + role, "en");
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

    private async Task<string> CodeAsync(Person who)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == who.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    /// <summary>A company on Net-30 credit, a shipped order charged its grand total, and its Live tax invoice (counter last).</summary>
    private async Task<Invoice> InvoiceAsync()
    {
        await using var db = Db();
        var seller = await InvoiceTestIssues.SellerAsync(db);
        var company = await InvoiceTestIssues.ActiveCompanyAsync(db);
        company.SetCredit(CreditTerms.Net30, Money.FromMinorUnits(1_000_000_000));
        await db.SaveChangesAsync();
        await using var tx = await db.Database.BeginTransactionAsync();
        var (order, shipment) = await InvoiceTestIssues.ShippedOrderAsync(db, seller, company);
        db.CreditLedgerEntries.Add(new CreditLedger(company, []).Charge(order.Id, order.GrandTotalMinor, DateTime.UtcNow, DateTime.UtcNow).Entry);
        var invoice = InvoiceTestIssues.Build(order, shipment, seller,
            await new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance).TakeAsync(seller, InvoiceSeries.TaxInvoice), company);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return invoice;
    }

    private static Task<HttpResponseMessage> SendAsync(Person who, HttpMethod method, string url, object? body = null, string? key = null, string? code = null)
    {
        var request = new HttpRequestMessage(method, url) { Content = JsonContent.Create(body ?? new { }) };
        if (key is not null) request.Headers.Add("Idempotency-Key", key);
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return who.Client.SendAsync(request);
    }

    private static Task<HttpResponseMessage> RecordAsync(Person who, Guid invoiceId, string number, long amountMinor = 50_000, string? key = null) =>
        SendAsync(who, HttpMethod.Post, $"/api/v1/admin/invoices/{invoiceId}/withholdings",
            new { kind = "vds", certificateNumber = number, certificateDate = DateOnly.FromDateTime(DateTime.UtcNow.AddHours(6)).ToString("yyyy-MM-dd"), amountMinor },
            key ?? "wh-" + Guid.NewGuid().ToString("N"));

    private static Task<HttpResponseMessage> VerifyAsync(Person who, Guid id, string? code) =>
        SendAsync(who, HttpMethod.Post, $"/api/v1/admin/withholdings/{id}/verify", code: code);

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
    [InlineData(Permissions.Roles.Support, false, false, true, false)]
    [InlineData(Permissions.Roles.Finance, true, true, true, false)]
    [InlineData(Permissions.Roles.Admin, true, true, true, true)]
    [InlineData(Permissions.Roles.Moderator, false, false, false, false)]
    [InlineData(Permissions.Roles.Buyer, false, false, false, false)]
    [InlineData(Permissions.Roles.Seller, false, false, false, false)]
    public void WithholdingPermissions_PerRole(string role, bool record, bool verify, bool read, bool entityFlag)
    {
        var granted = Permissions.GetDefaultPermissionsForRole(role);
        (granted.Contains(Permissions.CreditRecordPayment), granted.Contains(Permissions.CreditVerifyPayment), granted.Contains(Permissions.InvoicesView),
            granted.Contains(Permissions.CompaniesManage)).Should().Be((record, verify, read, entityFlag));
    }

    [PostgresFact]
    public async Task Withholdings_OverHttp_FourEyesStepUpAndDuplicates()
    {
        factory.CreateClient().Dispose();   // the host (and its Development seeding) first
        var invoice = await InvoiceAsync();
        var (support, financeA, financeB, admin, buyer) = (await PersonAsync(Permissions.Roles.Support), await PersonAsync(Permissions.Roles.Finance),
            await PersonAsync(Permissions.Roles.Finance), await PersonAsync(Permissions.Roles.Admin), await PersonAsync(Permissions.Roles.Buyer));

        (await RecordAsync(support, invoice.Id, "VDS-H-1")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "Support reads, never records");
        (await RecordAsync(buyer, invoice.Id, "VDS-H-1")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await buyer.Client.GetAsync("/api/v1/admin/withholdings")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await RecordAsync(financeA, invoice.Id, "VDS-H-1", key: "x")).StatusCode.Should().Be(HttpStatusCode.BadRequest, "an Idempotency-Key is 8-100 characters");
        var recorded = await DataAsync(await RecordAsync(financeA, invoice.Id, "VDS-H-1", key: "wh-http-0001"), HttpStatusCode.Created);
        var id = recorded.GetProperty("id").GetGuid();
        (recorded.GetProperty("status").GetString(), recorded.GetProperty("invoiceNumber").GetString()).Should().Be(("recorded", invoice.Number));
        (await DataAsync(await RecordAsync(financeA, invoice.Id, "VDS-H-1", key: "wh-http-0001"), HttpStatusCode.OK)).GetProperty("id").GetGuid().Should().Be(id);
        (await ErrorCodeAsync(await RecordAsync(admin, invoice.Id, "vds h 1"), HttpStatusCode.Conflict)).Should().Be("WITHHOLDING_CERTIFICATE_DUPLICATE");
        var queue = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/withholdings?invoiceId={invoice.Id}"), HttpStatusCode.OK);
        queue.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(id);
        await DataAsync(await support.Client.GetAsync($"/api/v1/admin/withholdings/{id}"), HttpStatusCode.OK);

        (await VerifyAsync(support, id, await CodeAsync(support))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(await VerifyAsync(financeB, id, null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await ErrorCodeAsync(await VerifyAsync(financeB, id, "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        (await ErrorCodeAsync(await VerifyAsync(financeA, id, await CodeAsync(financeA)), HttpStatusCode.Conflict)).Should().Be("WITHHOLDING_FOUR_EYES");
        (await DataAsync(await VerifyAsync(financeB, id, await CodeAsync(financeB)), HttpStatusCode.OK)).GetProperty("status").GetString().Should().Be("verified");

        // The company hint: Admin only, with a code; it changes no amount.
        var url = $"/api/v1/admin/companies/{invoice.CompanyId}/withholding-entity";
        (await SendAsync(financeA, HttpMethod.Put, url, new { withholdingEntity = true }, code: await CodeAsync(financeA))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await ErrorCodeAsync(await SendAsync(admin, HttpMethod.Put, url, new { withholdingEntity = true }), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await DataAsync(await SendAsync(admin, HttpMethod.Put, url, new { withholdingEntity = true }, code: await CodeAsync(admin)), HttpStatusCode.OK))
            .GetProperty("withholdingEntity").GetBoolean().Should().BeTrue();

        await using var db = Db();
        (await db.CreditLedgerEntries.CountAsync(l => l.WithholdingId == id)).Should().Be(1);
        (await db.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == financeB.UserId).Select(a => a.NewValuesJson).ToListAsync())
            .Should().Contain(json => json!.Contains("invoice.withholding") && json.Contains($"withholdingId={id}"));
        (await db.AuditLogs.CountAsync(a => a.Action == "company.withholding_entity_changed" && a.EntityId == invoice.CompanyId.ToString())).Should().Be(1);
    }
}
