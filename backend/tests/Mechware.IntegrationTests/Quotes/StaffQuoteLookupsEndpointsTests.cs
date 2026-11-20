using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.StaffQuoteKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.18 over HTTP: what the quote editor needs from the API besides the draft routes: the saved shipping fees on the staff version, a
/// company's delivery sites, the people who may be the contact, the sellers of variants, and the seller / minimum order / pack size on the
/// variant lookup (quotes.edit may use it). Its own host, so its own login budget.
/// </summary>
public sealed class StaffQuoteLookupsEndpointsTests(RfqApiFactory factory) : IClassFixture<RfqApiFactory>
{
    private readonly StaffQuoteKit _kit = new(factory);

    private async Task<(Guid Company, Guid Owner, Guid Requester, Guid Buyer)> CompanyAsync()
    {
        var (owner, requester, buyer) = (await _kit.BuyerAsync(), await _kit.BuyerAsync(), await _kit.BuyerAsync());
        var company = await _kit.Rfq.CompanyAsync(owner, "Lookup Ltd " + Guid.NewGuid().ToString("N")[..6], true, (requester, CompanyRoles.Requester), (buyer, CompanyRoles.Buyer));
        return (company, owner, requester, buyer);
    }

    private async Task<Guid> SiteAsync(Guid company, string name, bool isDefault)
    {
        await using var db = _kit.Db();
        var site = new CompanySite(Guid.NewGuid(), company, name, "Karim Mia", "01711000000", "12 Tejgaon I/A", null, "Tejgaon", "Dhaka", "Dhaka", "1208", isDefault);
        db.CompanySites.Add(site);
        await db.SaveChangesAsync();
        return site.Id;
    }

    private async Task<Guid> SellerOfAsync(Guid variant)
    {
        await using var db = _kit.Db();
        return await (from v in db.ProductVariants join p in db.Products on v.ProductId equals p.Id where v.Id == variant select p.SellerId).SingleAsync();
    }

    [PostgresFact]
    public async Task AFixedDeliveryDraft_ComesBackWithItsSavedFees()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var w = await CompanyAsync();
        var variant = (await _kit.Rfq.VariantAsync(null)).VariantId;
        var seller = await SellerOfAsync(variant);
        var site = await SiteAsync(w.Company, "Plant", false);
        var quote = await CreateAsync(support.Client, new { companyId = w.Company, contactUserId = w.Buyer });
        var draft = (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/quotes/{quote}"))).GetProperty("versions")[0];
        draft.GetProperty("shippingFees").ValueKind.Should().Be(JsonValueKind.Null, "a calculated draft has no fees");

        (await support.Client.PutAsJsonAsync($"/api/v1/admin/quotes/{quote}/versions/1", new
        {
            rowVersion = draft.GetProperty("rowVersion").GetUInt32(),
            validityDays = 15,
            shippingMode = "fixed",
            shipToSiteId = site,
            shippingFees = new[] { new { sellerId = seller, shippingMinor = 35_050L } },
            lines = new[] { new { variantId = variant, quantity = 2, unitPriceMinor = 100_000L, lineNote = (string?)null, belowFloorReason = (string?)null, rfqLineId = (Guid?)null } },
        })).StatusCode.Should().Be(HttpStatusCode.OK);

        var fees = (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/quotes/{quote}"))).GetProperty("versions")[0].GetProperty("shippingFees");
        fees.GetArrayLength().Should().Be(1);
        (fees[0].GetProperty("sellerId").GetGuid(), fees[0].GetProperty("shippingMinor").GetInt64()).Should().Be((seller, 35_050L));
    }

    [PostgresFact]
    public async Task TheSitesOfACompany_DefaultFirst_WithoutPhoneOrContactName()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var w = await CompanyAsync();
        var (other, main) = (await SiteAsync(w.Company, "B yard", false), await SiteAsync(w.Company, "Head office", true));
        await SiteAsync((await CompanyAsync()).Company, "Not ours", true);

        var response = await support.Client.GetAsync($"/api/v1/admin/companies/{w.Company}/sites");
        var raw = await response.Content.ReadAsStringAsync();
        raw.Should().NotContain("01711000000").And.NotContain("Karim", "no phone number or contact name");
        var data = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/companies/{w.Company}/sites"));
        data.EnumerateArray().Select(s => s.GetProperty("id").GetGuid()).Should().Equal(main, other);
        data[0].GetProperty("district").GetString().Should().Be("Dhaka");
    }

    [PostgresFact]
    public async Task TheContacts_AreActiveAcceptedBuyersAndAdmins_Only()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var w = await CompanyAsync();
        var url = $"/api/v1/admin/companies/{w.Company}/quote-contacts";

        var data = await DataAsync(await support.Client.GetAsync(url));
        data.EnumerateArray().Select(c => c.GetProperty("userId").GetGuid()).Should().BeEquivalentTo([w.Owner, w.Buyer], "the Requester may not be a contact");
        var buyer = data.EnumerateArray().Single(c => c.GetProperty("userId").GetGuid() == w.Buyer);
        buyer.GetProperty("roles")[0].GetString().Should().Be("buyer");
        buyer.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["userId", "fullName", "email", "roles"]);

        await using var db = _kit.Db();
        await db.CompanyMembers.Where(m => m.CompanyId == w.Company && m.UserId == w.Buyer).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false));
        (await DataAsync(await support.Client.GetAsync(url))).EnumerateArray().Select(c => c.GetProperty("userId").GetGuid())
            .Should().Equal([w.Owner], "an inactive member is not offered");
    }

    [PostgresFact]
    public async Task TheSellersOfVariants_AreListed_UnknownIdsLeftOut_AndTheCountIsChecked()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var (v1, v2) = ((await _kit.Rfq.VariantAsync(null)).VariantId, (await _kit.Rfq.VariantAsync(null)).VariantId);
        var url = $"/api/v1/admin/quote-sellers?variantIds={v1}&variantIds={v2}&variantIds={v1}&variantIds={Guid.NewGuid()}";
        var data = await DataAsync(await support.Client.GetAsync(url));
        data.GetArrayLength().Should().Be(2);
        foreach (var row in data.EnumerateArray())
        {
            var variant = row.GetProperty("variantId").GetGuid();
            row.GetProperty("sellerId").GetGuid().Should().Be(await SellerOfAsync(variant));
            row.GetProperty("sellerName").GetString().Should().NotBeNullOrWhiteSpace();
            row.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["variantId", "sellerId", "sellerName"]);
        }

        (await support.Client.GetAsync("/api/v1/admin/quote-sellers")).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var many = string.Join("&", Enumerable.Range(0, 51).Select(_ => $"variantIds={Guid.NewGuid()}"));
        (await support.Client.GetAsync("/api/v1/admin/quote-sellers?" + many)).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [PostgresFact]
    public async Task TheVariantLookup_CarriesTheSellerMinimumAndPackSize()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var variant = await _kit.Rfq.VariantAsync(null);
        var data = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/variants?sku={variant.Sku}"));
        var row = data.GetProperty("items")[0];
        row.GetProperty("sellerId").GetGuid().Should().Be(await SellerOfAsync(variant.VariantId));
        row.GetProperty("minOrderQty").GetInt32().Should().BeGreaterThan(0);
        row.GetProperty("packSize").GetInt32().Should().BeGreaterThan(0);
    }

    [PostgresFact]
    public async Task TheLookups_NeedQuotesEdit_AnUnknownCompanyIs404_AndAnswersAreNoStore()
    {
        var roles = new[] { Permissions.Roles.Support, Permissions.Roles.Finance, Permissions.Roles.Admin, Permissions.Roles.Moderator, Permissions.Roles.Seller, Permissions.Roles.Buyer };
        var people = new Dictionary<string, RfqApiKit.Person>();
        foreach (var role in roles) people[role] = await _kit.Rfq.PersonAsync(role);
        string[] editors = [Permissions.Roles.Support, Permissions.Roles.Admin];
        var w = await CompanyAsync();
        var unknown = Guid.NewGuid();
        var routes = new (string Name, string Url, HttpStatusCode Allowed)[]
        {
            ("sites", $"/api/v1/admin/companies/{w.Company}/sites", HttpStatusCode.OK),
            ("sites of unknown", $"/api/v1/admin/companies/{unknown}/sites", HttpStatusCode.NotFound),
            ("contacts", $"/api/v1/admin/companies/{w.Company}/quote-contacts", HttpStatusCode.OK),
            ("contacts of unknown", $"/api/v1/admin/companies/{unknown}/quote-contacts", HttpStatusCode.NotFound),
            ("sellers", $"/api/v1/admin/quote-sellers?variantIds={unknown}", HttpStatusCode.OK),
        };
        foreach (var (name, url, allowed) in routes)
        {
            foreach (var role in roles)
            {
                var response = await people[role].Client.GetAsync(url);
                var permitted = editors.Contains(role);
                response.StatusCode.Should().Be(permitted ? allowed : HttpStatusCode.Forbidden, $"{role} on {name}");
                if (permitted) response.Headers.CacheControl!.NoStore.Should().BeTrue($"{role} on {name}");
            }

            using var anonymous = factory.CreateClient();
            (await anonymous.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, $"anonymous on {name}");
        }
    }
}
