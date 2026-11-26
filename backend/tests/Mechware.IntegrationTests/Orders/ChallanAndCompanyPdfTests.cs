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
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Invoicing;
using UglyToad.PdfPig;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// E6.4d over HTTP: the delivery challan (buyer + staff routes) and the company invoice PDF route. IDOR (one identical 404), no prices on the challan,
/// not before shipping, <c>private, no-store</c>, role matrix. Own class and host (Specimen invoicing).
/// </summary>
public sealed class ChallanAndCompanyPdfTests(InvoicePdfApiFactory factory) : IClassFixture<InvoicePdfApiFactory>
{
    private const string Password = "ChallanPdf#2026!";

    private readonly InvoicePdfKit _kit = new(factory);

    private sealed record Person(Guid Id, HttpClient Client);

    private sealed record World(Person OwnerA, Person BuyerA, Person RequesterA, Person OwnerB, Person Stranger, Person Personal, HttpClient Support, HttpClient Admin, Guid CompanyA,
        Guid ShippedA, Guid InvoiceA, Guid InvoiceB, Guid PackedA, Guid ShippedPersonal);

    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static World? _world;

    private async Task<Person> BuyerAsync()
    {
        var email = $"ch.{Guid.NewGuid():N}"[..22] + "@example.test";
        await using var db = _kit.Db();
        var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Challan Buyer", "en");
        user.VerifyEmail(DateTime.UtcNow);
        user.AssignRole(Guid.NewGuid(), Permissions.Roles.Buyer);
        db.Users.Add(user);
        await db.SaveChangesAsync();
        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password = Password });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Person(user.Id, client);
    }

    private async Task<Guid> CompanyAsync(Guid admin, string name, string bin, params (Guid User, CompanyRoles Roles)[] members)
    {
        await using var db = _kit.Db();
        var companies = new CompanyService(db);
        var id = (await companies.RegisterAsync(admin, new RegisterCompanyRequest(name, "TL-CH-" + bin[^5..], bin, null, null))).Id;
        foreach (var (user, roles) in members) await companies.AddAcceptedMemberAsync(admin, id, new AddMemberRequest(user, roles, null, null));
        await companies.AdminActivateAsync(id);
        await companies.AdminSetCreditAsync(id, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        return id;
    }

    private async Task EnsureIssuerSellerAsync()
    {
        await using var db = _kit.Db();
        if (await db.Sellers.AnyAsync(x => x.Id == InvoicePdfKit.IssuerId)) return;
        var user = User.Create($"invoicing-{Guid.NewGuid():N}@example.test", null, null, "Invoicing Seller");
        db.Users.Add(user);
        db.Sellers.Add(new Mechware.Domain.Sellers.Seller(InvoicePdfKit.IssuerId, user.Id, "Invoicing Issuer", "invoicing-issuer", null, null, null, 4.5m, 10, 99m, DateTime.UtcNow));
        await db.SaveChangesAsync();
    }

    private async Task<Guid> PackedAsync(Guid buyer, Guid? company)
    {
        await using var db = _kit.Db();
        var firm = company is { } c ? await db.Companies.AsNoTracking().SingleAsync(x => x.Id == c) : null;
        return (await InvoiceTestIssues.PackedOrderAsync(db, buyer, [InvoicePdfKit.IssuerId], firm)).Id;
    }

    private static async Task ShipAsync(HttpClient staff, Guid orderId)
    {
        using var detail = JsonDocument.Parse(await staff.GetStringAsync($"/api/v1/admin/orders/{orderId}"));
        var rowVersion = detail.RootElement.GetProperty("data").GetProperty("rowVersion").GetUInt32();
        var ship = await staff.PostAsJsonAsync($"/api/v1/admin/orders/{orderId}/ship", new { rowVersion, courierName = "Steadfast", trackingNumber = "SF-6-4", trackingUrl = (string?)null });
        ship.StatusCode.Should().Be(HttpStatusCode.OK, await ship.Content.ReadAsStringAsync());
    }

    private async Task<Guid> InvoiceOfAsync(Guid orderId)
    {
        await using var db = _kit.Db();
        return await db.Invoices.Where(i => i.OrderId == orderId).Select(i => i.Id).SingleAsync();
    }

    private async Task<World> WorldAsync()
    {
        await Gate.WaitAsync();
        try
        {
            if (_world is not null) return _world;
            var support = (await _kit.LoginAsync(Permissions.Roles.Support)).Client;
            var admin = (await _kit.LoginAsync(Permissions.Roles.Admin)).Client;
            await EnsureIssuerSellerAsync();
            var (ownerA, buyerA, requesterA, ownerB, stranger, personal) = (await BuyerAsync(), await BuyerAsync(), await BuyerAsync(), await BuyerAsync(), await BuyerAsync(), await BuyerAsync());
            var companyA = await CompanyAsync(ownerA.Id, "Challan A Ltd", "5510000000123", (buyerA.Id, CompanyRoles.Buyer), (requesterA.Id, CompanyRoles.Requester));
            var companyB = await CompanyAsync(ownerB.Id, "Challan B Ltd", "5510000000456");
            var (shippedA, shippedB, packedA, shippedP) = (await PackedAsync(buyerA.Id, companyA), await PackedAsync(ownerB.Id, companyB), await PackedAsync(buyerA.Id, companyA), await PackedAsync(personal.Id, null));
            foreach (var id in new[] { shippedA, shippedB, shippedP }) await ShipAsync(support, id);
            return _world = new World(ownerA, buyerA, requesterA, ownerB, stranger, personal, support, admin, companyA, shippedA, await InvoiceOfAsync(shippedA), await InvoiceOfAsync(shippedB), packedA, shippedP);
        }
        finally
        {
            Gate.Release();
        }
    }

    private static string ErrorShape(string body)
    {
        var error = JsonDocument.Parse(body).RootElement.GetProperty("error");
        return error.GetProperty("code").GetString() + "|" + error.GetProperty("message").GetString();
    }

    private static void NoStore(HttpResponseMessage r) => (r.Headers.CacheControl is { NoStore: true, Private: true }).Should().BeTrue("answer must be private, no-store (was: " + r.Headers.CacheControl + ")");

    private static string Text(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return string.Join("\n", doc.GetPages().Select(p => p.Text));
    }

    private static async Task<byte[]> PdfAsync(HttpClient client, string url)
    {
        var r = await client.GetAsync(url);
        r.StatusCode.Should().Be(HttpStatusCode.OK, url + ": " + await r.Content.ReadAsStringAsync());
        r.Content.Headers.ContentType!.MediaType.Should().Be("application/pdf");
        NoStore(r);
        return await r.Content.ReadAsByteArrayAsync();
    }

    [PostgresFact]
    public async Task BuyerChallan_ThePlacerAndCompanyAdminGetIt_WithNoPrices_InBothLanguages_AndNoStore()
    {
        var w = await WorldAsync();
        string orderNumber;
        await using (var db = _kit.Db()) orderNumber = await db.Orders.Where(o => o.Id == w.ShippedA).Select(o => o.OrderNumber).SingleAsync();
        foreach (var who in new[] { w.BuyerA.Client, w.OwnerA.Client })
        {
            var en = Text(await PdfAsync(who, $"/api/v1/orders/{w.ShippedA}/challan"));
            en.Should().Contain("MW-CH-" + orderNumber[3..]).And.Contain("PO-2026/77").And.Contain("Steadfast").And.Contain("PLC-1200").And.Contain("Challan A Ltd");
            foreach (var price in new[] { "VAT", "BDT", "Price", "Total (", "45,500", "4,550", "123.45" }) en.Should().NotContain(price);
            var bn = Text(await PdfAsync(who, $"/api/v1/orders/{w.ShippedA}/challan?locale=bn"));
            bn.Should().Contain("PLC-1200").And.NotContain("VAT").And.NotContain("৪৫,৫০০");
        }

        (await w.BuyerA.Client.GetAsync($"/api/v1/orders/{w.ShippedA}/challan")).Headers.Vary.Should().Contain("Authorization");
        (await PdfAsync(w.Personal.Client, $"/api/v1/orders/{w.ShippedPersonal}/challan")).Should().NotBeEmpty();
    }

    [PostgresFact]
    public async Task BuyerChallan_EveryoneElse_GetsTheSame404()
    {
        var w = await WorldAsync();
        var callers = new Dictionary<string, HttpClient>
        {
            ["requester of the same company"] = w.RequesterA.Client,
            ["another company's admin"] = w.OwnerB.Client,
            ["a stranger"] = w.Stranger.Client,
            ["a person with a personal order"] = w.Personal.Client,
            ["support staff on the buyer route"] = w.Support
        };
        string? shape = null;
        foreach (var (who, client) in callers)
            foreach (var id in new[] { w.ShippedA, w.PackedA, Guid.NewGuid() })
            {
                var r = await client.GetAsync($"/api/v1/orders/{id}/challan");
                var body = await r.Content.ReadAsStringAsync();
                r.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{who}: {id}: {body}");
                NoStore(r);
                (shape ??= ErrorShape(body)).Should().Be(ErrorShape(body), who);
            }

        (await factory.CreateClient().GetAsync($"/api/v1/orders/{w.ShippedA}/challan")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.CreateClient().GetAsync($"/api/v1/admin/orders/{w.ShippedA}/challan")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [PostgresFact]
    public async Task Challan_IsNotAvailableBeforeShipping_ForThoseWhoMaySeeTheOrder()
    {
        var w = await WorldAsync();
        foreach (var url in new[] { $"/api/v1/orders/{w.PackedA}/challan", $"/api/v1/admin/orders/{w.PackedA}/challan" })
        {
            var r = await (url.Contains("/admin/") ? w.Support : w.BuyerA.Client).GetAsync(url);
            var body = await r.Content.ReadAsStringAsync();
            r.StatusCode.Should().Be(HttpStatusCode.Conflict, body);
            ErrorShape(body).Should().StartWith("CHALLAN_NOT_AVAILABLE|");
            NoStore(r);
        }
    }

    [PostgresFact]
    public async Task StaffChallan_FulfilOrViewAllGetsIt_BuyersGet403_BadLocale400_UnknownOrder404()
    {
        var w = await WorldAsync();
        (await PdfAsync(w.Support, $"/api/v1/admin/orders/{w.ShippedA}/challan")).Should().NotBeEmpty();
        (await PdfAsync(w.Admin, $"/api/v1/admin/orders/{w.ShippedA}/challan?locale=bn")).Should().NotBeEmpty();
        var forbidden = await w.OwnerA.Client.GetAsync($"/api/v1/admin/orders/{w.ShippedA}/challan");
        forbidden.StatusCode.Should().Be(HttpStatusCode.Forbidden);
        NoStore(forbidden);
        (await w.Support.GetAsync($"/api/v1/admin/orders/{w.ShippedA}/challan?locale=fr")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await w.Support.GetAsync($"/api/v1/admin/orders/{Guid.NewGuid()}/challan")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task CompanyInvoicePdf_AdminAndBuyerGetAColleaguesInvoice_EveryoneElseTheSame404()
    {
        var w = await WorldAsync();
        var own = await PdfAsync(w.BuyerA.Client, $"/api/v1/orders/{w.ShippedA}/invoices/{w.InvoiceA}/pdf");
        foreach (var member in new[] { w.OwnerA.Client, w.BuyerA.Client })
        {
            var r = await member.GetAsync($"/api/v1/companies/{w.CompanyA}/invoices/{w.InvoiceA}/pdf");
            r.StatusCode.Should().Be(HttpStatusCode.OK, await r.Content.ReadAsStringAsync());
            NoStore(r);
            r.Headers.Vary.Should().Contain("Authorization");
            (await r.Content.ReadAsByteArrayAsync()).Should().Equal(own, "the same stored, hash-checked file");
        }

        string? shape = null;
        var cases = new (string Who, HttpClient Client, string Url)[]
        {
            ("requester", w.RequesterA.Client, $"/api/v1/companies/{w.CompanyA}/invoices/{w.InvoiceA}/pdf"),
            ("another company's admin", w.OwnerB.Client, $"/api/v1/companies/{w.CompanyA}/invoices/{w.InvoiceA}/pdf"),
            ("stranger", w.Stranger.Client, $"/api/v1/companies/{w.CompanyA}/invoices/{w.InvoiceA}/pdf"),
            ("staff on the company route", w.Admin, $"/api/v1/companies/{w.CompanyA}/invoices/{w.InvoiceA}/pdf"),
            ("admin with another company's invoice", w.OwnerA.Client, $"/api/v1/companies/{w.CompanyA}/invoices/{w.InvoiceB}/pdf"),
            ("admin with an unknown invoice", w.OwnerA.Client, $"/api/v1/companies/{w.CompanyA}/invoices/{Guid.NewGuid()}/pdf"),
            ("admin of an unknown company", w.OwnerA.Client, $"/api/v1/companies/{Guid.NewGuid()}/invoices/{w.InvoiceA}/pdf")
        };
        foreach (var (who, client, url) in cases)
        {
            var r = await client.GetAsync(url);
            var body = await r.Content.ReadAsStringAsync();
            r.StatusCode.Should().Be(HttpStatusCode.NotFound, $"{who}: {body}");
            NoStore(r);
            (shape ??= ErrorShape(body)).Should().Be(ErrorShape(body), who);
        }

        (await factory.CreateClient().GetAsync(cases[0].Url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
