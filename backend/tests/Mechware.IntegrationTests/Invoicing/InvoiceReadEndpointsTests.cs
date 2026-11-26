using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Mechware.Application.Companies;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Invoicing;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// E6.8a over HTTP (ADR-0012 §14; threat IDOR): the buyer routes <c>orders/{id}/invoices</c> and <c>companies/{id}/invoices</c> (+ detail) and the
/// staff routes <c>admin/invoices</c> (+ detail), on a real host with real sign-ins. Its own class, so its own host and login budget. One world is
/// built once: company A (Admin, Buyer, Requester members), company B, a person with a personal order, one seller, six invoices in all.
/// Every refusal of a buyer route is the same 404 body; the company routes need Admin or Buyer (<c>Credit:BalanceVisibleTo</c>), the order routes the placer.
/// </summary>
public sealed class InvoiceReadEndpointsTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Password = "InvoiceRead#2026!";
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Person(Guid Id, HttpClient Client);

    private sealed record Issued(Guid OrderId, string OrderNumber, Guid InvoiceId, string Number);

    private sealed record World(Person OwnerA, Person BuyerA, Person RequesterA, Person OwnerB, Person Personal, Person Support, Person Finance, Person Admin, Person Moderator,
        Person Stranger, Guid CompanyA, Guid CompanyB, string BinA, Issued A1, Issued A2, Issued CreditNote, Issued A3Specimen, Issued B1, Issued P1);

    private static readonly SemaphoreSlim _gate = new(1, 1);
    private static World? _world;

    private async Task<Person> PersonAsync(string role)
    {
        var email = $"ir.{Guid.NewGuid():N}"[..22] + "@example.test";
        var staff = role != Permissions.Roles.Buyer;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Invoice Read " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            if (staff)
            {
                user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
                user.EnableTwoFactor(DateTime.UtcNow);
            }
            db.Users.Add(user);
            await db.SaveChangesAsync();
            var client = factory.CreateClient();
            var login = await client.PostAsJsonAsync("/api/v1/auth/login", staff
                ? new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) }
                : (object)new { identifier = email, password = Password });
            login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
            using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
            return new Person(user.Id, client);
        }
    }

    private async Task<Guid> CompanyAsync(Guid admin, string name, string bin, params (Guid User, CompanyRoles Roles)[] members)
    {
        await using var db = Db();
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(admin, new RegisterCompanyRequest(name, "TL-IR-" + bin[^5..], bin, null, null))).Id;
        foreach (var (user, roles) in members) await companies.AddAcceptedMemberAsync(admin, id, new AddMemberRequest(user, roles, null, null));
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        return id;
    }

    /// <summary>A shipped order placed by <paramref name="placer"/> and its invoice, issued the way ship does (counter last), in one transaction.</summary>
    private async Task<Issued> IssueAsync(Guid seller, Guid placer, Guid? company, InvoiceSeries series = InvoiceSeries.TaxInvoice)
    {
        await using var db = Db();
        var firm = company is { } c ? await db.Companies.AsNoTracking().SingleAsync(x => x.Id == c) : null;
        await using var tx = await db.Database.BeginTransactionAsync();
        var order = await InvoiceTestIssues.PackedOrderAsync(db, placer, [seller], firm);
        var shipment = order.Ship("Steadfast", $"SF-{Guid.NewGuid():N}"[..20], null, placer, DateTime.UtcNow);
        db.Set<Shipment>().Add(shipment);
        await db.SaveChangesAsync();
        var taken = await new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance).TakeAsync(seller, series);
        var invoice = InvoiceTestIssues.Build(order, shipment, seller, taken, firm);
        db.Invoices.Add(invoice);
        await db.SaveChangesAsync();
        await tx.CommitAsync();
        return new Issued(order.Id, order.OrderNumber, invoice.Id, invoice.Number);
    }

    private async Task<World> WorldAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_world is not null) return _world;
            factory.CreateClient().Dispose();
            var (ownerA, buyerA, requesterA, ownerB, personal, support, finance, admin, moderator, stranger) = (await PersonAsync(Permissions.Roles.Buyer), await PersonAsync(Permissions.Roles.Buyer),
                await PersonAsync(Permissions.Roles.Buyer), await PersonAsync(Permissions.Roles.Buyer), await PersonAsync(Permissions.Roles.Buyer), await PersonAsync(Permissions.Roles.Support),
                await PersonAsync(Permissions.Roles.Finance), await PersonAsync(Permissions.Roles.Admin), await PersonAsync(Permissions.Roles.Moderator), await PersonAsync(Permissions.Roles.Buyer));
            const string binA = "4410000000123", binB = "4410000000456";
            var companyA = await CompanyAsync(ownerA.Id, "Invoice Read A Ltd", binA, (buyerA.Id, CompanyRoles.Buyer), (requesterA.Id, CompanyRoles.Requester));
            var companyB = await CompanyAsync(ownerB.Id, "Invoice Read B Ltd", binB);
            Guid seller;
            await using (var db = Db()) seller = await InvoiceTestIssues.SellerAsync(db);

            var a1 = await IssueAsync(seller, buyerA.Id, companyA);
            var a2 = await IssueAsync(seller, ownerA.Id, companyA);
            var a3 = await IssueAsync(seller, buyerA.Id, companyA, InvoiceSeries.SpecimenTaxInvoice);
            var b1 = await IssueAsync(seller, ownerB.Id, companyB);
            var p1 = await IssueAsync(seller, personal.Id, null);
            Issued note;
            await using (var db = Db())
            {
                await using var tx = await db.Database.BeginTransactionAsync();
                var taken = await new InvoiceNumberAllocator(db, NullLogger<InvoiceNumberAllocator>.Instance).TakeAsync(seller, InvoiceSeries.CreditNote);
                await InvoiceTestIssues.CreditNoteAsync(db, a2.InvoiceId, taken, ownerB.Id);
                await tx.CommitAsync();
                var row = await db.Invoices.AsNoTracking().SingleAsync(i => i.OriginalInvoiceId == a2.InvoiceId);
                note = new Issued(a2.OrderId, a2.OrderNumber, row.Id, row.Number);
            }
            await using (var db = Db())
            {
                var entity = await db.Companies.SingleAsync(c => c.Id == companyA);
                var lines = await db.CreditLedgerEntries.Where(l => l.CompanyId == companyA).ToListAsync();
                var at = DateTime.UtcNow.AddDays(-2);
                db.CreditLedgerEntries.Add(new CreditLedger(entity, lines).Charge(a1.OrderId, 40_000, at, at).Entry);
                await db.SaveChangesAsync();
            }
            return _world = new World(ownerA, buyerA, requesterA, ownerB, personal, support, finance, admin, moderator, stranger, companyA, companyB, binA, a1, a2, note, a3, b1, p1);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<(HttpStatusCode Status, string Body, HttpResponseMessage Response)> GetAsync(HttpClient client, string url)
    {
        var r = await client.GetAsync(url);
        return (r.StatusCode, await r.Content.ReadAsStringAsync(), r);
    }

    private static JsonElement Data(string body) => JsonDocument.Parse(body).RootElement.GetProperty("data").Clone();

    private static string ErrorShape(string body)
    {
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        return error.GetProperty("code").GetString() + "|" + error.GetProperty("message").GetString();
    }

    private static void NoStore(HttpResponseMessage r) =>
        (r.Headers.CacheControl is { NoStore: true, Private: true }).Should().BeTrue("every invoice answer is private, no-store (was: " + r.Headers.CacheControl + ")");

    private static string[] Keys(JsonElement e) => [.. e.EnumerateObject().Select(p => p.Name).Order()];

    private static async Task<JsonElement> OkAsync(HttpClient client, string url, bool noStore = true)
    {
        var (status, body, response) = await GetAsync(client, url);
        status.Should().Be(HttpStatusCode.OK, url + ": " + body);
        if (noStore) NoStore(response);
        return Data(body);
    }

    [PostgresFact]
    public async Task OrderRoutes_ThePlacer_SeesTheOrdersInvoices_WithTheFieldWhitelist_AndTheBinMasked()
    {
        var w = await WorldAsync();
        var list = await OkAsync(w.BuyerA.Client, $"/api/v1/orders/{w.A1.OrderId}/invoices");
        list.GetArrayLength().Should().Be(1);
        Keys(list[0]).Should().Equal("currency", "id", "issuedAtUtc", "kind", "mode", "netMinor", "number", "orderId", "orderNumber", "originalInvoiceId", "originalNumber", "sdMinor", "totalMinor", "vatMinor");
        list[0].GetProperty("number").GetString().Should().Be(w.A1.Number);
        list[0].GetProperty("kind").GetString().Should().Be("tax_invoice");
        list[0].GetProperty("mode").GetString().Should().Be("live");

        var (status, body, response) = await GetAsync(w.BuyerA.Client, $"/api/v1/orders/{w.A1.OrderId}/invoices/{w.A1.InvoiceId}");
        status.Should().Be(HttpStatusCode.OK, body);
        NoStore(response);
        var detail = Data(body);
        Keys(detail).Should().Equal("buyer", "delivery", "invoice", "lines", "purchaseOrderNumber", "quoteNumber", "seller");
        Keys(detail.GetProperty("buyer")).Should().Equal("address", "bin", "name");
        Keys(detail.GetProperty("lines")[0]).Should().Equal("descriptionBn", "descriptionEn", "discountMinor", "displayUnitPriceMinor", "grossMinor", "kind", "lineNo", "netMinor", "partNumber",
            "quantity", "sdMinor", "sdRate", "sku", "totalMinor", "unit", "unitPriceMinor", "vatMinor", "vatRate");
        detail.GetProperty("buyer").GetProperty("bin").GetString().Should().Be("*********0123", "a buyer sees the last four digits only");
        body.Should().NotContain(w.BinA, "the whole buyer BIN is on the PDF and with staff, not in a buyer's JSON");
        foreach (var forbidden in new[] { "contentHash", "issuedBy", "shipmentId", "subOrderId", "sellerId", "companyId", "consultantChecked", "series", "serial", "cost", "note" })
            body.Should().NotContainEquivalentOf("\"" + forbidden + "\"");
        detail.GetProperty("lines").GetArrayLength().Should().BeGreaterThan(1);
    }

    [PostgresFact]
    public async Task OrderRoutes_EveryoneElse_GetsTheSame404_EvenInTheSameCompany()
    {
        var w = await WorldAsync();
        var callers = new Dictionary<string, HttpClient>
        {
            ["a member of the same company who did not place it"] = w.OwnerA.Client,
            ["a requester of the same company"] = w.RequesterA.Client,
            ["another company's admin"] = w.OwnerB.Client,
            ["a person with a personal order"] = w.Personal.Client,
            ["a stranger"] = w.Stranger.Client,
            ["support staff"] = w.Support.Client,
            ["finance staff"] = w.Finance.Client,
            ["admin staff"] = w.Admin.Client
        };
        var urls = new[]
        {
            $"/api/v1/orders/{w.A1.OrderId}/invoices", $"/api/v1/orders/{w.A1.OrderId}/invoices/{w.A1.InvoiceId}",
            $"/api/v1/orders/{Guid.NewGuid()}/invoices", $"/api/v1/orders/{Guid.NewGuid()}/invoices/{Guid.NewGuid()}"
        };
        string? shape = null;
        foreach (var (who, client) in callers)
            foreach (var url in urls)
            {
                var (status, body, response) = await GetAsync(client, url);
                status.Should().Be(HttpStatusCode.NotFound, $"{who}: {url}: {body}");
                NoStore(response);
                (shape ??= ErrorShape(body)).Should().Be(ErrorShape(body), $"{who}: {url} must look like every other refusal");
            }

        // The placer: another order's invoice id under their own order, an unknown invoice id and anonymous callers.
        foreach (var url in new[] { $"/api/v1/orders/{w.A1.OrderId}/invoices/{w.A2.InvoiceId}", $"/api/v1/orders/{w.A1.OrderId}/invoices/{Guid.NewGuid()}" })
        {
            var (status, body, response) = await GetAsync(w.BuyerA.Client, url);
            status.Should().Be(HttpStatusCode.NotFound, body);
            NoStore(response);
            ErrorShape(body).Should().Be(shape);
        }
        (await GetAsync(factory.CreateClient(), urls[0])).Status.Should().Be(HttpStatusCode.Unauthorized);
        (await GetAsync(factory.CreateClient(), $"/api/v1/companies/{w.CompanyA}/invoices")).Status.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task OrderRoute_AnOrderWithNoInvoiceYet_IsAnEmptyList_ForItsPlacer()
    {
        var w = await WorldAsync();
        Guid orderId;
        await using (var db = Db()) orderId = (await InvoiceTestIssues.PackedOrderAsync(db, w.Personal.Id, [await InvoiceTestIssues.SellerAsync(db)])).Id;
        (await OkAsync(w.Personal.Client, $"/api/v1/orders/{orderId}/invoices")).GetArrayLength().Should().Be(0);
        (await GetAsync(w.Stranger.Client, $"/api/v1/orders/{orderId}/invoices")).Status.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task CompanyRoutes_RoleMatrix_AdminAndBuyerSee_EveryoneElseGetsTheSame404()
    {
        var w = await WorldAsync();
        string url(Guid company) => $"/api/v1/companies/{company}/invoices";
        string detail(Guid company, Guid invoice) => $"/api/v1/companies/{company}/invoices/{invoice}";

        (await OkAsync(w.OwnerA.Client, url(w.CompanyA))).GetProperty("totalCount").GetInt32().Should().Be(4);
        (await OkAsync(w.BuyerA.Client, url(w.CompanyA))).GetProperty("totalCount").GetInt32().Should().Be(4);
        (await OkAsync(w.OwnerA.Client, detail(w.CompanyA, w.A1.InvoiceId))).GetProperty("invoice").GetProperty("number").GetString().Should().Be(w.A1.Number);
        (await OkAsync(w.BuyerA.Client, detail(w.CompanyA, w.A2.InvoiceId))).GetProperty("invoice").GetProperty("number").GetString().Should().Be(w.A2.Number);

        var refused = new Dictionary<string, HttpClient>
        {
            ["a Requester of the company"] = w.RequesterA.Client,
            ["another company's admin"] = w.OwnerB.Client,
            ["a person without a company"] = w.Personal.Client,
            ["support staff"] = w.Support.Client,
            ["finance staff"] = w.Finance.Client,
            ["admin staff"] = w.Admin.Client
        };
        string? shape = null;
        foreach (var (who, client) in refused)
            foreach (var u in new[] { url(w.CompanyA), detail(w.CompanyA, w.A1.InvoiceId), url(Guid.NewGuid()), detail(Guid.NewGuid(), Guid.NewGuid()) })
            {
                var (status, body, response) = await GetAsync(client, u);
                status.Should().Be(HttpStatusCode.NotFound, $"{who}: {u}: {body}");
                NoStore(response);
                (shape ??= ErrorShape(body)).Should().Be(ErrorShape(body), $"{who}: {u}");
            }

        // A member of company A asking for company B's invoice (or a personal one) under A's id or under B's id is the same 404.
        foreach (var u in new[] { detail(w.CompanyA, w.B1.InvoiceId), detail(w.CompanyA, w.P1.InvoiceId), url(w.CompanyB), detail(w.CompanyB, w.B1.InvoiceId) })
        {
            var (status, body, _) = await GetAsync(w.OwnerA.Client, u);
            status.Should().Be(HttpStatusCode.NotFound, u + ": " + body);
            ErrorShape(body).Should().Be(shape);
        }
    }

    [PostgresFact]
    public async Task CompanyList_NeverCountsOrShowsForeignRows_AndPagesAndFiltersByDhakaDate()
    {
        var w = await WorldAsync();
        var url = $"/api/v1/companies/{w.CompanyA}/invoices";
        var all = await OkAsync(w.OwnerA.Client, url);
        all.GetProperty("totalCount").GetInt32().Should().Be(4);
        var numbers = all.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("number").GetString()).ToList();
        numbers.Should().BeEquivalentTo([w.A1.Number, w.A2.Number, w.CreditNote.Number, w.A3Specimen.Number]);
        numbers.Should().NotContain([w.B1.Number, w.P1.Number]);
        var note = all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("number").GetString() == w.CreditNote.Number);
        (note.GetProperty("kind").GetString(), note.GetProperty("originalNumber").GetString()).Should().Be(("credit_note", w.A2.Number));
        all.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("number").GetString() == w.A3Specimen.Number).GetProperty("mode").GetString().Should().Be("specimen");
        (await OkAsync(w.OwnerB.Client, $"/api/v1/companies/{w.CompanyB}/invoices")).GetProperty("totalCount").GetInt32().Should().Be(1);

        var paged = await OkAsync(w.OwnerA.Client, url + "?pageSize=3");
        (paged.GetProperty("items").GetArrayLength(), paged.GetProperty("totalCount").GetInt32(), paged.GetProperty("pageSize").GetInt32()).Should().Be((3, 4, 3));
        (await OkAsync(w.OwnerA.Client, url + "?pageSize=3&page=2")).GetProperty("items").GetArrayLength().Should().Be(1);
        (await OkAsync(w.OwnerA.Client, url + "?pageSize=500")).GetProperty("pageSize").GetInt32().Should().Be(50);

        var today = DateTime.UtcNow.AddHours(6).ToString("yyyy-MM-dd");
        var tomorrow = DateTime.UtcNow.AddHours(6).AddDays(1).ToString("yyyy-MM-dd");
        (await OkAsync(w.OwnerA.Client, $"{url}?from={today}&to={today}")).GetProperty("totalCount").GetInt32().Should().Be(4);
        (await OkAsync(w.OwnerA.Client, $"{url}?from={tomorrow}")).GetProperty("totalCount").GetInt32().Should().Be(0);
        foreach (var bad in new[] { "?from=yesterday", $"?from={tomorrow}&to={today}" })
            (await GetAsync(w.OwnerA.Client, url + bad)).Status.Should().Be(HttpStatusCode.BadRequest);
        (await GetAsync(w.OwnerB.Client, url + "?from=yesterday")).Status.Should().Be(HttpStatusCode.NotFound, "access is decided before the filters are read");
    }

    [PostgresFact]
    public async Task StaffRoutes_SupportFinanceAdmin_Read_EveryoneElseIsRefused()
    {
        var w = await WorldAsync();
        foreach (var staff in new[] { w.Support, w.Finance, w.Admin })
        {
            (await OkAsync(staff.Client, "/api/v1/admin/invoices")).GetProperty("totalCount").GetInt32().Should().Be(6);
            (await OkAsync(staff.Client, $"/api/v1/admin/invoices/{w.B1.InvoiceId}")).GetProperty("detail").GetProperty("invoice").GetProperty("number").GetString().Should().Be(w.B1.Number);
        }
        foreach (var who in new[] { w.Moderator, w.OwnerA, w.Stranger })
            foreach (var url in new[] { "/api/v1/admin/invoices", $"/api/v1/admin/invoices/{w.A1.InvoiceId}" })
                (await GetAsync(who.Client, url)).Status.Should().Be(HttpStatusCode.Forbidden, url);
        (await GetAsync(factory.CreateClient(), "/api/v1/admin/invoices")).Status.Should().Be(HttpStatusCode.Unauthorized);
        var (status, body, response) = await GetAsync(w.Support.Client, $"/api/v1/admin/invoices/{Guid.NewGuid()}");
        status.Should().Be(HttpStatusCode.NotFound, body);
        NoStore(response);
    }

    [PostgresFact]
    public async Task StaffList_FiltersAndPaging_AndTheWhitelistOfListAndDetail_WithTheWholeBin()
    {
        var w = await WorldAsync();
        async Task<int> CountAsync(string query) => (await OkAsync(w.Finance.Client, "/api/v1/admin/invoices" + query)).GetProperty("totalCount").GetInt32();

        (await CountAsync($"?number={w.A1.Number}")).Should().Be(1);
        (await CountAsync($"?number={w.A1.Number[..8].ToLowerInvariant()}")).Should().BeGreaterThan(1, "prefix, any case");
        (await CountAsync($"?orderNumber={w.B1.OrderNumber}")).Should().Be(1);
        (await CountAsync($"?companyId={w.CompanyA}")).Should().Be(4);
        (await CountAsync($"?companyId={w.CompanyB}")).Should().Be(1);
        (await CountAsync($"?companyId={Guid.NewGuid()}")).Should().Be(0);
        (await CountAsync("?kind=credit_note")).Should().Be(1);
        (await CountAsync("?kind=tax_invoice")).Should().Be(5);
        (await CountAsync("?mode=specimen")).Should().Be(1);
        (await CountAsync("?mode=live&kind=tax_invoice")).Should().Be(4);
        var today = DateTime.UtcNow.AddHours(6).ToString("yyyy-MM-dd");
        (await CountAsync($"?from={today}&to={today}")).Should().Be(6);
        (await CountAsync($"?from={DateTime.UtcNow.AddHours(6).AddDays(1):yyyy-MM-dd}")).Should().Be(0);
        foreach (var bad in new[] { "?kind=refund", "?mode=weekend", "?from=soon", "?number=%27%3B--", $"?from={DateTime.UtcNow.AddDays(3):yyyy-MM-dd}&to={today}" })
            (await GetAsync(w.Finance.Client, "/api/v1/admin/invoices" + bad)).Status.Should().Be(HttpStatusCode.BadRequest, bad);

        var page = await OkAsync(w.Finance.Client, "/api/v1/admin/invoices?pageSize=4");
        (page.GetProperty("items").GetArrayLength(), page.GetProperty("totalCount").GetInt32()).Should().Be((4, 6));
        (await OkAsync(w.Finance.Client, "/api/v1/admin/invoices?pageSize=4&page=2")).GetProperty("items").GetArrayLength().Should().Be(2);
        (await OkAsync(w.Finance.Client, "/api/v1/admin/invoices?pageSize=1000")).GetProperty("pageSize").GetInt32().Should().Be(50);

        Keys(page.GetProperty("items")[0]).Should().Equal("buyerName", "companyId", "invoice", "sellerId");
        var (_, body, _) = await GetAsync(w.Finance.Client, $"/api/v1/admin/invoices/{w.A1.InvoiceId}");
        var detail = Data(body);
        Keys(detail).Should().Equal("companyId", "consultantChecked", "contentHash", "detail", "fiscalYear", "issuedBy", "sellerId", "serial", "series", "shipmentId", "subOrderId");
        Keys(detail.GetProperty("detail")).Should().Equal("buyer", "delivery", "invoice", "lines", "purchaseOrderNumber", "quoteNumber", "seller");
        detail.GetProperty("detail").GetProperty("buyer").GetProperty("bin").GetString().Should().Be(w.BinA, "staff see the whole BIN, decrypted");
        detail.GetProperty("contentHash").GetString().Should().MatchRegex("^[0-9a-f]{64}$");
        body.Should().NotContain("enc:", "no ciphertext ever leaves the server");
    }

    [PostgresFact]
    public async Task CreditStatementLines_ShowTheInvoiceNumber_ForBuyerAndStaff_AndOnlyOfThatCompanysOrders()
    {
        var w = await WorldAsync();
        var buyer = await OkAsync(w.OwnerA.Client, $"/api/v1/companies/{w.CompanyA}/credit/statement");
        var line = buyer.GetProperty("lines").GetProperty("items").EnumerateArray().Single(l => l.GetProperty("orderNumber").GetString() == w.A1.OrderNumber);
        line.GetProperty("invoiceNumbers").EnumerateArray().Select(n => n.GetString()).Should().Equal(w.A1.Number);

        var staff = await OkAsync(w.Finance.Client, $"/api/v1/admin/companies/{w.CompanyA}/credit/statement", noStore: false);
        staff.GetProperty("lines").GetProperty("items").EnumerateArray().Single(l => l.GetProperty("orderNumber").GetString() == w.A1.OrderNumber)
            .GetProperty("invoiceNumbers").EnumerateArray().Select(n => n.GetString()).Should().Equal(w.A1.Number);
        (await GetAsync(w.OwnerB.Client, $"/api/v1/companies/{w.CompanyA}/credit/statement")).Status.Should().Be(HttpStatusCode.NotFound);
    }
}
