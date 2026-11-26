using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Catalog;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>Own host for the PDF routes (Specimen mode, so shipping issues a SPEC invoice).</summary>
public sealed class InvoicePdfApiFactory() : DbBackedFactory(settings: new Dictionary<string, string> { ["Invoicing:Mode"] = "Specimen", ["Invoicing:IssuerSellerId"] = InvoicePdfKit.IssuerId.ToString() });

/// <summary>Same host with a tiny <c>InvoiceRead</c> budget.</summary>
public sealed class InvoiceReadLimitFactory() : DbBackedFactory(settings: new Dictionary<string, string> { ["Invoicing:Mode"] = "Specimen", ["Invoicing:IssuerSellerId"] = InvoicePdfKit.IssuerId.ToString(), ["RateLimiting:InvoiceReadPermitPerMinute"] = "3" });

/// <summary>Shared sign-in and shipping helpers for the invoice PDF tests.</summary>
public sealed class InvoicePdfKit(DbBackedFactory factory)
{
    /// <summary>The one seller that issues in these hosts: Specimen numbers are SPEC-… for every seller, so a second seller would collide on number.</summary>
    public static readonly Guid IssuerId = new("6e64b000-0000-4000-8000-000000000001");

    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "InvoiceStaff#2026!";

    public MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    public async Task<(HttpClient Client, Guid UserId)> LoginAsync(string role)
    {
        factory.CreateClient().Dispose();
        var email = $"ipdf.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Invoice " + role, "en");
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
        return (client, id);
    }

    /// <summary>A packed order of <paramref name="buyerId"/> shipped over HTTP by <paramref name="staff"/>; returns the order and its one invoice.</summary>
    public async Task<(Guid OrderId, Guid InvoiceId)> ShippedAsync(HttpClient staff, Guid buyerId)
    {
        Guid orderId;
        await using (var db = Db())
        {
            if (!await db.Sellers.AnyAsync(x => x.Id == IssuerId))
            {
                var user = User.Create($"invoicing-{Guid.NewGuid():N}@example.test", null, null, "Invoicing Seller");
                db.Users.Add(user);
                db.Sellers.Add(new Mechware.Domain.Sellers.Seller(IssuerId, user.Id, "Invoicing Issuer", "invoicing-issuer", null, null, null, 4.5m, 10, 99m, DateTime.UtcNow));
                await db.SaveChangesAsync();
            }

            orderId = (await InvoiceTestIssues.PackedOrderAsync(db, buyerId, [IssuerId])).Id;
        }

        var detail = await staff.GetAsync($"/api/v1/admin/orders/{orderId}");
        using var doc = JsonDocument.Parse(await detail.Content.ReadAsStringAsync());
        var rowVersion = doc.RootElement.GetProperty("data").GetProperty("rowVersion").GetUInt32();
        var ship = await staff.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/ship", new { rowVersion, courierName = "Steadfast", trackingNumber = "SF-6-4", trackingUrl = (string?)null });
        ship.StatusCode.Should().Be(HttpStatusCode.OK, await ship.Content.ReadAsStringAsync());
        await using var read = Db();
        return (orderId, await read.Invoices.Where(i => i.OrderId == orderId).Select(i => i.Id).SingleAsync());
    }
}

/// <summary>
/// E6.4b over HTTP (ADR-0012 §12, §14; threat model IT5, IT6): eager render by the outbox handler, lazy fallback, SHA-256 null to value once and checked on
/// every download, the buyer's one 404, the staff route and the document check, <c>private, no-store</c> on every answer. Own class and host.
/// </summary>
public sealed class InvoicePdfEndpointsTests(InvoicePdfApiFactory factory) : IClassFixture<InvoicePdfApiFactory>
{
    private readonly InvoicePdfKit _kit = new(factory);

    private IObjectStorage Storage => factory.Services.GetRequiredService<IObjectStorage>();

    private static string BuyerUrl(Guid order, Guid invoice, string tail = "") => $"/api/v1/orders/{order}/invoices/{invoice}/pdf{tail}";

    private static string StaffUrl(Guid invoice, string tail = "") => $"/api/v1/admin/invoices/{invoice}/pdf{tail}";

    private static string Hex(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));

    private async Task<(HttpClient Buyer, HttpClient Staff, Guid OrderId, Guid InvoiceId, Guid BuyerId)> WorldAsync()
    {
        var buyer = await _kit.LoginAsync(Permissions.Roles.Buyer);
        var staff = await _kit.LoginAsync(Permissions.Roles.Support);
        var (order, invoice) = await _kit.ShippedAsync(staff.Client, buyer.UserId);
        return (buyer.Client, staff.Client, order, invoice, buyer.UserId);
    }

    private async Task<(string? Key, string? Sha)> RecordedAsync(Guid invoice, string locale)
    {
        await using var db = _kit.Db();
        var query = locale == "en"
            ? db.Database.SqlQuery<string>($"SELECT coalesce(pdf_en_key, '') || '|' || coalesce(pdf_en_sha256, '') AS \"Value\" FROM invoices WHERE id = {invoice}")
            : db.Database.SqlQuery<string>($"SELECT coalesce(pdf_bn_key, '') || '|' || coalesce(pdf_bn_sha256, '') AS \"Value\" FROM invoices WHERE id = {invoice}");
        var row = (await query.ToListAsync()).Single().Split('|');
        return (row[0].Length == 0 ? null : row[0], row[1].Length == 0 ? null : row[1]);
    }

    private async Task<int> AuditCountAsync(string action, Guid invoice)
    {
        await using var db = _kit.Db();
        return await db.AuditLogs.CountAsync(a => a.Action == action && a.EntityId == invoice.ToString());
    }

    private async Task RunOutboxAsync()
    {
        await using var scope = factory.Services.CreateAsyncScope();
        await scope.ServiceProvider.GetRequiredService<OutboxRelay>().ProcessBatchAsync(500);
    }

    [PostgresFact]
    public async Task TheOutboxRendersBothLanguagesAfterCommit_RecordsTheShaOnce_AndTheLazyDownloadServesTheSameFile()
    {
        var w = await WorldAsync();
        (await RecordedAsync(w.InvoiceId, "en")).Should().Be((null, null), "nothing is rendered inside the ship transaction");

        await RunOutboxAsync();
        foreach (var locale in new[] { "en", "bn" })
        {
            var (key, sha) = await RecordedAsync(w.InvoiceId, locale);
            key.Should().Be($"invoices/{w.InvoiceId:N}/{locale}.pdf");
            sha.Should().MatchRegex("^[0-9a-f]{64}$");
            var stored = await Storage.OpenReadAsync(key!);
            using var ms = new MemoryStream();
            await stored!.CopyToAsync(ms);
            Hex(ms.ToArray()).Should().Be(sha);

            var response = await w.Buyer.GetAsync(BuyerUrl(w.OrderId, w.InvoiceId, "?locale=" + locale));
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
            response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            Hex(await response.Content.ReadAsByteArrayAsync()).Should().Be(sha);
        }

        (await AuditCountAsync("invoice.pdf_rendered", w.InvoiceId)).Should().Be(2, "once per language");
        await RunOutboxAsync();
        (await AuditCountAsync("invoice.pdf_rendered", w.InvoiceId)).Should().Be(2);

        // The database refuses to rewrite a recorded SHA (ADR-0012 §9).
        await using var db = _kit.Db();
        await FluentActions.Awaiting(() => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE invoices SET pdf_en_sha256 = {new string('a', 64)} WHERE id = {w.InvoiceId}"))
            .Should().ThrowAsync<Exception>();
    }

    [PostgresFact]
    public async Task TheFirstDownload_RendersLazily_WhenTheOutboxHasNotRun_AndTheKeyComesFromTheServer()
    {
        var w = await WorldAsync();
        var response = await w.Staff.GetAsync(StaffUrl(w.InvoiceId, "?locale=bn"));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var (key, sha) = await RecordedAsync(w.InvoiceId, "bn");
        key.Should().Be($"invoices/{w.InvoiceId:N}/bn.pdf");
        Hex(await response.Content.ReadAsByteArrayAsync()).Should().Be(sha);

        // A key in the query string changes nothing: the file is the recorded one.
        var forged = await w.Staff.GetAsync(StaffUrl(w.InvoiceId, "?locale=bn&key=invoices/other/bn.pdf&sha256=" + new string('0', 64)));
        forged.StatusCode.Should().Be(HttpStatusCode.OK);
        Hex(await forged.Content.ReadAsByteArrayAsync()).Should().Be(sha);
        (await w.Staff.GetAsync(StaffUrl(w.InvoiceId, "?locale=fr"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task ATamperedStoredInvoicePdf_IsNotServed_AndIsAudited()
    {
        var w = await WorldAsync();
        await RunOutboxAsync();
        var (key, sha) = await RecordedAsync(w.InvoiceId, "en");
        await Storage.DeleteAsync(key!);
        var forgery = "%PDF-1.4 forged invoice"u8.ToArray();
        await Storage.PutAsync(key!, new MemoryStream(forgery));

        foreach (var response in new[] { await w.Buyer.GetAsync(BuyerUrl(w.OrderId, w.InvoiceId)), await w.Staff.GetAsync(StaffUrl(w.InvoiceId)) })
        {
            response.StatusCode.Should().Be(HttpStatusCode.ServiceUnavailable);
            var body = await response.Content.ReadAsStringAsync();
            body.Should().Contain("INVOICE_PDF_UNAVAILABLE").And.NotContain("forged");
            response.Content.Headers.ContentType!.MediaType.Should().NotBe("application/pdf");
        }

        (await AuditCountAsync("invoice.pdf_integrity_failed", w.InvoiceId)).Should().Be(2);
        await using var db = _kit.Db();
        var details = await db.AuditLogs.Where(a => a.Action == "invoice.pdf_integrity_failed" && a.EntityId == w.InvoiceId.ToString()).Select(a => a.NewValuesJson).FirstAsync();
        details.Should().Contain(sha!).And.Contain(Hex(forgery));
        (await RecordedAsync(w.InvoiceId, "en")).Sha.Should().Be(sha, "the record is never changed");
    }

    [PostgresFact]
    public async Task AMissingStoredFile_IsRenderedAgain_AndServedOnlyWithTheRecordedHash()
    {
        var w = await WorldAsync();
        await RunOutboxAsync();
        var (key, sha) = await RecordedAsync(w.InvoiceId, "en");
        await Storage.DeleteAsync(key!);

        var response = await w.Buyer.GetAsync(BuyerUrl(w.OrderId, w.InvoiceId));
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        Hex(await response.Content.ReadAsByteArrayAsync()).Should().Be(sha);
        (await Storage.OpenReadAsync(key!)).Should().NotBeNull("the file is put back");
    }

    [PostgresFact]
    public async Task LookupBySha_FindsTheIssuedInvoice_AndNothingElse()
    {
        var w = await WorldAsync();
        await RunOutboxAsync();
        var (_, en) = await RecordedAsync(w.InvoiceId, "en");
        var (_, bn) = await RecordedAsync(w.InvoiceId, "bn");
        string number;
        await using (var db = _kit.Db()) number = await db.Invoices.Where(i => i.Id == w.InvoiceId).Select(i => i.Number).SingleAsync();

        foreach (var (sha, locale) in new[] { (en!, "en"), (bn!.ToUpperInvariant(), "bn") })
        {
            var hit = await w.Staff.GetAsync($"/api/v1/admin/invoice-documents/{sha}");
            hit.StatusCode.Should().Be(HttpStatusCode.OK);
            using var doc = JsonDocument.Parse(await hit.Content.ReadAsStringAsync());
            var data = doc.RootElement.GetProperty("data");
            data.GetProperty("invoiceId").GetGuid().Should().Be(w.InvoiceId);
            data.GetProperty("number").GetString().Should().Be(number);
            data.GetProperty("kind").GetString().Should().Be("tax_invoice");
            data.GetProperty("locale").GetString().Should().Be(locale);
        }

        var bodies = new List<string>();
        foreach (var other in new[] { new string('0', 64), "not-a-hash", new string('a', 63), Hex("some other file"u8.ToArray()) })
        {
            var miss = await w.Staff.GetAsync($"/api/v1/admin/invoice-documents/{other}");
            miss.StatusCode.Should().Be(HttpStatusCode.NotFound);
            bodies.Add(await miss.Content.ReadAsStringAsync());
        }

        bodies.Select(b => System.Text.RegularExpressions.Regex.Replace(b, "\"(traceId|correlationId|instance)\":\"[^\"]*\"", "")).Distinct().Should().HaveCount(1);
        (await w.Buyer.GetAsync($"/api/v1/admin/invoice-documents/{en}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, "buyers have no invoices.view");
    }

    [PostgresFact]
    public async Task Idor_InvoicesOfAnotherCompany_Are404()
    {
        var w = await WorldAsync();
        var other = await _kit.LoginAsync(Permissions.Roles.Buyer);
        var staff = await _kit.LoginAsync(Permissions.Roles.Support);
        var (otherOrder, otherInvoice) = await _kit.ShippedAsync(staff.Client, other.UserId);

        var answers = new[]
        {
            await other.Client.GetAsync(BuyerUrl(w.OrderId, w.InvoiceId)),          // someone else's order and invoice
            await other.Client.GetAsync(BuyerUrl(otherOrder, w.InvoiceId)),         // my order, their invoice
            await w.Buyer.GetAsync(BuyerUrl(w.OrderId, otherInvoice)),              // their invoice under my order
            await w.Buyer.GetAsync(BuyerUrl(Guid.NewGuid(), w.InvoiceId)),          // unknown order
            await w.Buyer.GetAsync(BuyerUrl(w.OrderId, Guid.NewGuid())),            // unknown invoice
            await w.Staff.GetAsync(BuyerUrl(w.OrderId, w.InvoiceId)),               // staff are not the buyer either
        };
        foreach (var a in answers) a.StatusCode.Should().Be(HttpStatusCode.NotFound);
        var bodies = new List<string>();
        foreach (var a in answers) bodies.Add(System.Text.RegularExpressions.Regex.Replace(await a.Content.ReadAsStringAsync(), "\"(traceId|correlationId|instance)\":\"[^\"]*\"", ""));
        bodies.Distinct().Should().HaveCount(1, "everything the caller may not see is one and the same answer");

        (await w.Buyer.GetAsync(BuyerUrl(w.OrderId, w.InvoiceId))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await other.Client.GetAsync(StaffUrl(w.InvoiceId))).StatusCode.Should().Be(HttpStatusCode.Forbidden, "the staff route needs invoices.view");
        (await factory.CreateClient().GetAsync(BuyerUrl(w.OrderId, w.InvoiceId))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task InvoiceEndpoints_SendPrivateNoStore()
    {
        var w = await WorldAsync();
        await RunOutboxAsync();
        var (_, sha) = await RecordedAsync(w.InvoiceId, "en");
        var responses = new[]
        {
            await w.Buyer.GetAsync(BuyerUrl(w.OrderId, w.InvoiceId)),
            await w.Buyer.GetAsync(BuyerUrl(w.OrderId, Guid.NewGuid())),
            await w.Buyer.GetAsync(BuyerUrl(w.OrderId, w.InvoiceId, "?locale=xx")),
            await w.Staff.GetAsync(StaffUrl(w.InvoiceId)),
            await w.Staff.GetAsync(StaffUrl(Guid.NewGuid())),
            await w.Staff.GetAsync($"/api/v1/admin/invoice-documents/{sha}"),
            await w.Staff.GetAsync($"/api/v1/admin/invoice-documents/{new string('f', 64)}"),
        };
        foreach (var r in responses)
        {
            r.Headers.CacheControl!.Private.Should().BeTrue();
            r.Headers.CacheControl.NoStore.Should().BeTrue();
            r.Headers.CacheControl.Public.Should().BeFalse();
        }

        responses[0].Headers.Vary.Should().Contain("Authorization");
        responses[1].Headers.Vary.Should().Contain("Authorization");
    }
}

/// <summary>E6.4b: the PDF routes share the per-user <c>InvoiceRead</c> budget (own host, limit 3).</summary>
public sealed class InvoicePdfRateLimitTests(InvoiceReadLimitFactory factory) : IClassFixture<InvoiceReadLimitFactory>
{
    [PostgresFact]
    public async Task InvoicePdf_IsRateLimited()
    {
        var kit = new InvoicePdfKit(factory);
        var (buyer, other) = (await kit.LoginAsync(Permissions.Roles.Buyer), await kit.LoginAsync(Permissions.Roles.Buyer));
        var url = $"/api/v1/orders/{Guid.NewGuid()}/invoices/{Guid.NewGuid()}/pdf";

        for (var i = 0; i < 3; i++) (await buyer.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await buyer.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await other.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound, "the budget is per user");
    }
}
