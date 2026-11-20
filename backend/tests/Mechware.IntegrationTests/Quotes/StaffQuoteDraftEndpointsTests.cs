using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.StaffQuoteKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.7 over HTTP: the draft editor (<c>PUT admin/quotes/{id}/versions/{n}</c>) and the price preview (ADR-0011 §4, §5, §9, §12; threat
/// model QT3, QT6, QT7). Real host, real staff sign-ins with authenticator codes; buyers never sign in. Its own class, so its own login budget.
/// </summary>
public sealed class StaffQuoteDraftEndpointsTests(RfqApiFactory factory) : IClassFixture<RfqApiFactory>
{
    private readonly StaffQuoteKit _kit = new(factory);

    private sealed record World(Guid Company, Guid Owner, Guid Requester, Guid Buyer);

    private async Task<World> CompanyAsync(string name)
    {
        var (owner, requester, buyer) = (await _kit.BuyerAsync(), await _kit.BuyerAsync(), await _kit.BuyerAsync());
        var company = await _kit.Rfq.CompanyAsync(owner, name + " " + Guid.NewGuid().ToString("N")[..6], true, (requester, CompanyRoles.Requester),
            (buyer, CompanyRoles.Buyer));
        return new World(company, owner, requester, buyer);
    }

    private async Task<Guid> VariantAsync() => (await _kit.Rfq.VariantAsync(null)).VariantId;

    private async Task<Guid> SellerOfAsync(Guid variant)
    {
        await using var db = _kit.Db();
        return await (from v in db.ProductVariants join p in db.Products on v.ProductId equals p.Id where v.Id == variant select p.SellerId).SingleAsync();
    }

    /// <summary>The public price per unit on the net basis (what the order path charges a company with no price list).</summary>
    private async Task<long> NetPriceAsync(Guid variant)
    {
        await using var db = _kit.Db();
        var row = await (from v in db.ProductVariants
                         join p in db.Products on v.ProductId equals p.Id
                         join c in db.Categories on p.CategoryId equals c.Id
                         where v.Id == variant
                         select new { v.Price, c.VatRate }).SingleAsync();
        return PriceResolver.ToOrderBasis(row.Price.ToMinorUnits(), row.VatRate, TaxMode.Exclusive);
    }

    private async Task<Guid> SiteAsync(Guid company, string name)
    {
        await using var db = _kit.Db();
        var site = new CompanySite(Guid.NewGuid(), company, name, "Karim Mia", "01711000000", "12 Tejgaon I/A", "Gate 2", "Tejgaon", "Dhaka", "Dhaka", "1208", false);
        db.CompanySites.Add(site);
        await db.SaveChangesAsync();
        return site.Id;
    }

    private async Task SqlAsync(FormattableString sql)
    {
        await using var db = _kit.Db();
        await db.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    /// <summary>The owner's 10 % global floor, whatever an earlier test of this database left.</summary>
    private async Task TenPercentGlobalFloorAsync()
    {
        await using var db = _kit.Db();
        var global = await db.MarginFloors.FirstOrDefaultAsync(f => f.Scope == MarginFloorScope.Global);
        if (global is null) db.MarginFloors.Add(MarginFloor.Create(Guid.NewGuid(), MarginFloorScope.Global, null, null, 10m, Guid.NewGuid(), DateTime.UtcNow));
        else global.ChangeMarkup(10m, Guid.NewGuid(), DateTime.UtcNow);
        await db.SaveChangesAsync();
    }

    private static string Url(Guid quote, int n, string tail = "") => $"/api/v1/admin/quotes/{quote}/versions/{n}{tail}";

    private static Task<HttpResponseMessage> PutAsync(HttpClient c, Guid quote, int n, object body) => c.PutAsJsonAsync(Url(quote, n), body);

    private static Task<HttpResponseMessage> PreviewAsync(HttpClient c, Guid quote, int n, object body) => PostAsync(c, Url(quote, n, "/price-preview"), body);

    private static async Task<JsonElement> VersionAsync(HttpClient c, Guid quote, int n = 1) =>
        (await DataAsync(await c.GetAsync($"/api/v1/admin/quotes/{quote}"))).GetProperty("versions")[n - 1];

    private static uint RowVersion(JsonElement version) => version.GetProperty("rowVersion").GetUInt32();

    private static object LineOf(Guid variant, int quantity, long price, string? note = null, string? reason = null, Guid? rfqLineId = null) =>
        new { variantId = variant, quantity, unitPriceMinor = price, lineNote = note, belowFloorReason = reason, rfqLineId };

    private static object Body(uint? rowVersion, params object[] lines) => new { rowVersion, validityDays = 15, lines };

    /// <summary>The stored content of a version: what a refused or preview-only request must leave alone.</summary>
    private static string Fingerprint(JsonElement v) => $"{v.GetProperty("rowVersion")}|{v.GetProperty("validityDays")}|{v.GetProperty("paymentTerms")}|" +
        string.Join(",", v.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("variantId")}:{l.GetProperty("quantity")}:{l.GetProperty("unitPriceMinor")}")) +
        "|" + string.Join(",", v.GetProperty("contributors").EnumerateArray().Select(c => c.GetGuid()));

    [PostgresFact]
    public async Task EditingADraft_ReplacesHeaderAndLines_WithTheServersSnapshot_AndRecordsEveryEditorAsAContributor()
    {
        var (support, admin) = (await _kit.Rfq.PersonAsync(Permissions.Roles.Support), await _kit.Rfq.PersonAsync(Permissions.Roles.Admin));
        var w = await CompanyAsync("Draft Edit Ltd");
        var (v1, v2) = (await VariantAsync(), await VariantAsync());
        var rfq = await _kit.RfqAsync(w.Company, w.Requester, (v1, 3));
        var quote = await CreateAsync(support.Client, new { rfqId = rfq });
        var created = await VersionAsync(support.Client, quote);
        var rfqLine = created.GetProperty("lines")[0].GetProperty("rfqLineId").GetGuid();
        (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs/{rfq}"))).GetProperty("lines")[0].GetProperty("id").GetGuid()
            .Should().Be(rfqLine, "the RFQ detail names its lines, so the editor can link a quote line to one");
        var site = await SiteAsync(w.Company, "Tejgaon plant");
        var (seller1, seller2) = (await SellerOfAsync(v1), await SellerOfAsync(v2));

        var saved = await DataAsync(await PutAsync(support.Client, quote, 1, new
        {
            rowVersion = RowVersion(created),
            validityDays = 20,
            paymentTerms = "advance",
            shippingMode = "fixed",
            shipToSiteId = site,
            shippingFees = new[] { new { sellerId = seller1, shippingMinor = 5_000L }, new { sellerId = seller2, shippingMinor = 0L } },
            termsText = "Prices in BDT.",
            buyerNote = "As discussed\u0007 on the phone.",
            internalNote = "Margin is thin.",
            lines = new[] { LineOf(v1, 4, 90_000, "Ex stock", rfqLineId: rfqLine), LineOf(v2, 2, 120_000) },
        }));
        var v = saved.GetProperty("versions")[0];
        (v.GetProperty("validityDays").GetInt32(), v.GetProperty("paymentTerms").GetString(), v.GetProperty("shippingMode").GetString(),
                v.GetProperty("shipToSiteId").GetGuid(), v.GetProperty("buyerNote").GetString(), v.GetProperty("internalNote").GetString())
            .Should().Be((20, "advance", "fixed", site, "As discussed on the phone.", "Margin is thin."), "control characters are stripped (QT17)");
        var shipTo = v.GetProperty("shipTo");
        (shipTo.GetProperty("siteName").GetString(), shipTo.GetProperty("line1").GetString(), shipTo.GetProperty("line2").GetString(),
            shipTo.GetProperty("postalCode").GetString()).Should().Be(("Tejgaon plant", "12 Tejgaon I/A", "Gate 2", "1208"), "the site is copied by the server");
        var lines = v.GetProperty("lines").EnumerateArray().ToList();
        lines.Select(l => (l.GetProperty("lineNo").GetInt32(), l.GetProperty("variantId").GetGuid(), l.GetProperty("quantity").GetInt32(),
            l.GetProperty("unitPriceMinor").GetInt64())).Should().Equal((1, v1, 4, 90_000L), (2, v2, 2, 120_000L));
        lines[0].GetProperty("rfqLineId").GetGuid().Should().Be(rfqLine);
        lines[0].GetProperty("lineNote").GetString().Should().Be("Ex stock");
        lines[1].GetProperty("publicUnitPriceMinor").GetInt64().Should().Be(await NetPriceAsync(v2), "the public price is the server's");
        lines[0].GetProperty("titleEn").GetString().Should().Be("Test Product");
        lines.Sum(l => l.GetProperty("lineNetMinor").GetInt64()).Should().Be(v.GetProperty("goodsNetMinor").GetInt64()).And.Be(4 * 90_000 + 2 * 120_000);
        (v.GetProperty("shippingTotalMinor").GetInt64(), v.GetProperty("grandTotalMinor").GetInt64())
            .Should().Be((5_000, v.GetProperty("goodsTotalMinor").GetInt64() + 5_000));
        RowVersion(v).Should().NotBe(RowVersion(created), "every save gives a new rowVersion");

        // A second editor: also a contributor (so neither may approve this version later), recorded once each.
        await DataAsync(await PutAsync(admin.Client, quote, 1, Body(RowVersion(v), LineOf(v1, 4, 91_000))));
        var again = await DataAsync(await PutAsync(admin.Client, quote, 1, Body(RowVersion(await VersionAsync(admin.Client, quote)), LineOf(v1, 4, 92_000))));
        again.GetProperty("versions")[0].GetProperty("contributors").EnumerateArray().Select(c => c.GetGuid()).Should().Equal(support.Id, admin.Id);
        again.GetProperty("versions")[0].GetProperty("shippingMode").GetString().Should().Be("calculated", "a PUT replaces the whole content");
        again.GetProperty("versions")[0].GetProperty("shipTo").ValueKind.Should().Be(JsonValueKind.Null);

        await using var db = _kit.Db();
        (await db.AuditLogs.CountAsync(a => a.Action == "quote.draft_changed" && a.EntityId == quote.ToString())).Should().Be(3);
        (await db.AuditLogs.CountAsync(a => a.Action == "quote.draft_changed" && a.EntityId == quote.ToString() && a.ActorId == admin.Id)).Should().Be(2);
        (await db.QuoteVersions.Where(x => x.QuoteId == quote).SelectMany(x => x.Lines).CountAsync()).Should().Be(1, "replaced lines are gone from the draft");
    }

    [PostgresFact]
    public async Task TheEditor_Refuses51Lines_ADuplicateItem_ABadPackQuantity_AndOtherBadContent_AndChangesNothing()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var w = await CompanyAsync("Draft Refusals Ltd");
        var other = await CompanyAsync("Draft Refusals Other Ltd");
        var (v1, packed, gone) = (await VariantAsync(), await VariantAsync(), await VariantAsync());
        await SqlAsync($"UPDATE product_variants SET min_order_qty = 5, pack_size = 5 WHERE id = {packed}");
        await SqlAsync($"UPDATE product_variants SET status = 'inactive' WHERE id = {gone}");
        var quote = await CreateAsync(support.Client, new { companyId = w.Company, contactUserId = w.Buyer });
        var foreignRfqLine = (await VersionAsync(support.Client, await CreateAsync(support.Client,
            new { rfqId = await _kit.RfqAsync(other.Company, other.Requester, (v1, 1)) }))).GetProperty("lines")[0].GetProperty("rfqLineId").GetGuid();
        var foreignSite = await SiteAsync(other.Company, "Other plant");
        var before = await VersionAsync(support.Client, quote);
        var rv = RowVersion(before);
        var many = new List<object>();
        for (var i = 0; i < 51; i++) many.Add(LineOf(await VariantAsync(), 1, 10_000));

        async Task<string> RefusedAsync(object body, HttpStatusCode status = HttpStatusCode.BadRequest)
        {
            var r = await PutAsync(support.Client, quote, 1, body);
            var text = await r.Content.ReadAsStringAsync();
            r.StatusCode.Should().Be(status, text);
            return text;
        }

        (await RefusedAsync(Body(rv, [.. many]))).Should().Contain("at most 50 lines", "51 lines cannot become one order");
        (await RefusedAsync(Body(rv, LineOf(v1, 1, 10_000), LineOf(v1, 2, 10_000)))).Should().Contain("one line only");
        (await RefusedAsync(Body(rv, LineOf(packed, 7, 10_000)))).Should().Contain("packs of 5", "the order path's pack rule applies to quote lines");
        (await RefusedAsync(Body(rv, LineOf(packed, 3, 10_000)))).Should().Contain("minimum order is 5");
        (await RefusedAsync(Body(rv, LineOf(gone, 1, 10_000)), HttpStatusCode.Conflict)).Should().Contain("VARIANT_UNAVAILABLE");
        (await RefusedAsync(Body(rv, LineOf(Guid.NewGuid(), 1, 10_000)))).Should().Contain("Choose a catalogue item");
        (await RefusedAsync(Body(rv, LineOf(v1, 0, 10_000)))).Should().Contain("Quantity must be 1 to");
        (await RefusedAsync(Body(rv, LineOf(v1, 1, 0)))).Should().Contain("unit price must be positive");
        (await RefusedAsync(Body(rv, LineOf(v1, 1, 10_000, rfqLineId: foreignRfqLine)))).Should().Contain("own request");
        (await RefusedAsync(Body(null, LineOf(v1, 1, 10_000)))).Should().Contain("rowVersion");
        (await RefusedAsync(new { rowVersion = rv, validityDays = 91, lines = new[] { LineOf(v1, 1, 10_000) } })).Should().Contain("valid for 1 to 90 days");
        (await RefusedAsync(new { rowVersion = rv, validityDays = 15, paymentTerms = "credit_30" })).Should().Contain("paymentTerms");
        (await RefusedAsync(new
        {
            rowVersion = rv,
            validityDays = 15,
            shippingMode = "fixed",
            shipToSiteId = foreignSite,
            shippingFees = new[] { new { sellerId = await SellerOfAsync(v1), shippingMinor = 0L } },
            lines = new[] { LineOf(v1, 1, 10_000) }
        }))
            .Should().Contain("delivery site of this company");
        (await RefusedAsync(new { rowVersion = rv, validityDays = 15, shipToSiteId = await SiteAsync(w.Company, "Own plant") })).Should().Contain("Calculated delivery");
        (await RefusedAsync(new { rowVersion = rv, validityDays = 15, lines = new[] { LineOf(v1, 1, 10_000, reason: "no") } })).Should().Contain("reason of 5");

        Fingerprint(await VersionAsync(support.Client, quote)).Should().Be(Fingerprint(before), "a refused edit changes nothing");

        // A quote made from a request may link only that request's lines, never another request's (of this or another company).
        var fromRfq = await CreateAsync(support.Client, new { rfqId = await _kit.RfqAsync(w.Company, w.Requester, (v1, 1)) });
        var ownBefore = await VersionAsync(support.Client, fromRfq);
        var r2 = await PutAsync(support.Client, fromRfq, 1, Body(RowVersion(ownBefore), LineOf(v1, 1, 10_000, rfqLineId: foreignRfqLine)));
        r2.StatusCode.Should().Be(HttpStatusCode.BadRequest, await r2.Content.ReadAsStringAsync());
        (await r2.Content.ReadAsStringAsync()).Should().Contain("own request");
        Fingerprint(await VersionAsync(support.Client, fromRfq)).Should().Be(Fingerprint(ownBefore));
        await using var db = _kit.Db();
        (await db.AuditLogs.CountAsync(a => a.Action == "quote.draft_changed" && a.EntityId == quote.ToString())).Should().Be(0);
    }

    [PostgresFact]
    public async Task AStaleRowVersion_Is409QuoteVersionChanged_AndAVersionNoLongerADraft_IsNotEditable()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var w = await CompanyAsync("Draft Stale Ltd");
        var variant = await VariantAsync();
        var quote = await CreateAsync(support.Client, new { rfqId = await _kit.RfqAsync(w.Company, w.Requester, (variant, 2)) });
        var stale = RowVersion(await VersionAsync(support.Client, quote));
        var fresh = RowVersion((await DataAsync(await PutAsync(support.Client, quote, 1, Body(stale, LineOf(variant, 2, 50_000))))).GetProperty("versions")[0]);

        var before = await VersionAsync(support.Client, quote);
        (await CodeAsync(await PutAsync(support.Client, quote, 1, Body(stale, LineOf(variant, 2, 1_000))), HttpStatusCode.Conflict)).Should().Be("QUOTE_VERSION_CHANGED");
        Fingerprint(await VersionAsync(support.Client, quote)).Should().Be(Fingerprint(before), "the stale save wrote nothing");

        await _kit.SendAsync(quote);
        var sent = await VersionAsync(support.Client, quote);
        RowVersion(sent).Should().NotBe(fresh, "the send changed the row");
        (await CodeAsync(await PutAsync(support.Client, quote, 1, Body(fresh, LineOf(variant, 2, 1_000))), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_VERSION_CHANGED", "an edit against a send (ADR §9)");
        (await CodeAsync(await PutAsync(support.Client, quote, 1, Body(RowVersion(sent), LineOf(variant, 2, 1_000))), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_NOT_EDITABLE", "a sent version never changes; revise instead");
        (await CodeAsync(await PreviewAsync(support.Client, quote, 1, Body(null, LineOf(variant, 2, 1_000))), HttpStatusCode.Conflict)).Should().Be("QUOTE_NOT_EDITABLE");
        (await CodeAsync(await PutAsync(support.Client, quote, 7, Body(RowVersion(sent))), HttpStatusCode.BadRequest)).Should().NotBeEmpty("no such version");
        (await CodeAsync(await PutAsync(support.Client, Guid.NewGuid(), 1, Body(1)), HttpStatusCode.NotFound)).Should().NotBeEmpty();
    }

    [PostgresFact]
    public async Task AQuoteContact_MustBeAMemberOfTheQuotesCompany()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var w = await CompanyAsync("Draft Contact Ltd");
        var other = await CompanyAsync("Draft Contact Other Ltd");
        var variant = await VariantAsync();
        var quote = await CreateAsync(support.Client, new { companyId = w.Company, contactUserId = w.Buyer });
        object Contact(uint rv, Guid contact) => new { rowVersion = rv, contactUserId = contact, validityDays = 15, lines = new[] { LineOf(variant, 1, 40_000) } };

        var rv = RowVersion(await VersionAsync(support.Client, quote));
        foreach (var (who, why) in new[] { (other.Buyer, "a buyer of another company"), (w.Requester, "a member without the Buyer or Admin role"),
                     (Guid.NewGuid(), "nobody"), (support.Id, "a staff user") })
            (await CodeAsync(await PutAsync(support.Client, quote, 1, Contact(rv, who)), HttpStatusCode.BadRequest)).Should().NotBeEmpty(why);
        await using (var db = _kit.Db())
            (await db.Quotes.SingleAsync(q => q.Id == quote)).ContactUserId.Should().Be(w.Buyer, "refusals leave the contact");

        var changed = await DataAsync(await PutAsync(support.Client, quote, 1, Contact(rv, w.Owner)));
        changed.GetProperty("quote").GetProperty("contactUserId").GetGuid().Should().Be(w.Owner, "the company's Admin may be the contact");

        // Once a version is sent, its hash covers the contact: the contact is locked until that version is withdrawn or the quote closed.
        await _kit.SendAsync(quote);
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        var v2 = await VersionAsync(support.Client, quote, 2);
        (await CodeAsync(await PutAsync(support.Client, quote, 2, Contact(RowVersion(v2), w.Buyer)), HttpStatusCode.Conflict)).Should().Be("QUOTE_CONTACT_LOCKED");
        await DataAsync(await PutAsync(support.Client, quote, 2, Contact(RowVersion(v2), w.Owner)));   // the same contact: allowed
        await using (var db = _kit.Db())
        {
            var q = await LoadAsync(db, quote);
            q.ContactUserId.Should().Be(w.Owner);
            QuoteContentHash.Matches(q, q.Version(1), q.Version(1).ContentHash).Should().BeTrue("the sent version's hash still holds");
        }
    }

    [PostgresFact]
    public async Task AStaffMemberWhoBelongsToTheCompany_CannotEditApproveOrSendItsQuote()
    {
        var (insider, support) = (await _kit.Rfq.PersonAsync(Permissions.Roles.Support), await _kit.Rfq.PersonAsync(Permissions.Roles.Support));
        var owner = await _kit.BuyerAsync();
        var company = await _kit.Rfq.CompanyAsync(owner, "Draft Insider Ltd " + Guid.NewGuid().ToString("N")[..6], true, (insider.Id, CompanyRoles.Requester));
        var variant = await VariantAsync();
        var quote = await CreateAsync(support.Client, new { rfqId = await _kit.RfqAsync(company, owner, (variant, 1)) });
        var before = await VersionAsync(support.Client, quote);

        (await CodeAsync(await PutAsync(insider.Client, quote, 1, Body(RowVersion(before), LineOf(variant, 1, 1_000))), HttpStatusCode.Forbidden))
            .Should().Be("QUOTE_CONFLICT_OF_INTEREST", "edit half of QT6 (send and approve are D4.8)");
        (await CodeAsync(await PreviewAsync(insider.Client, quote, 1, Body(null, LineOf(variant, 1, 1_000))), HttpStatusCode.Forbidden))
            .Should().Be("QUOTE_CONFLICT_OF_INTEREST", "the preview shows the company's own price and the check figures");

        Fingerprint(await VersionAsync(support.Client, quote)).Should().Be(Fingerprint(before), "the insider is not a contributor and nothing changed");
        await using var db = _kit.Db();
        (await db.AuditLogs.CountAsync(a => a.Action == "quote.draft_changed" && a.ActorId == insider.Id)).Should().Be(0);
    }

    [PostgresFact]
    public async Task Revise_MakesANewVersion_AndLeavesTheSentOneUntouched()
    {
        var support = await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
        var w = await CompanyAsync("Draft Revise Ltd");
        var variant = await VariantAsync();
        var quote = await CreateAsync(support.Client, new { companyId = w.Company, contactUserId = w.Buyer });
        var site = await SiteAsync(w.Company, "Revise plant");
        await DataAsync(await PutAsync(support.Client, quote, 1, new
        {
            rowVersion = RowVersion(await VersionAsync(support.Client, quote)),
            validityDays = 15,
            shippingMode = "fixed",
            shipToSiteId = site,
            shippingFees = new[] { new { sellerId = await SellerOfAsync(variant), shippingMinor = 2_500L } },
            lines = new[] { LineOf(variant, 5, 60_000) },
        }));
        await _kit.SendAsync(quote);
        var sent = await VersionAsync(support.Client, quote);
        string Content(JsonElement v) => v.GetProperty("contentHash").GetString() + "|" + v.GetProperty("rowVersion") + "|" + v.GetProperty("shipTo") + "|" +
            string.Join(",", v.GetProperty("lines").EnumerateArray().Select(l => $"{l.GetProperty("id")}:{l.GetProperty("quantity")}:{l.GetProperty("unitPriceMinor")}"));

        var revised = await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        var draft = revised.GetProperty("versions")[1];
        (draft.GetProperty("status").GetString(), draft.GetProperty("shipTo").GetProperty("siteName").GetString()).Should().Be(("draft", "Revise plant"));
        var edited = await DataAsync(await PutAsync(support.Client, quote, 2, new
        {
            rowVersion = RowVersion(draft),
            validityDays = 10,
            shippingMode = "fixed",
            shipToSiteId = site,
            shippingFees = new[] { new { sellerId = await SellerOfAsync(variant), shippingMinor = 2_000L } },
            lines = new[] { LineOf(variant, 5, 55_000) },
        }));
        edited.GetProperty("versions")[1].GetProperty("lines")[0].GetProperty("unitPriceMinor").GetInt64().Should().Be(55_000);
        Content(edited.GetProperty("versions")[0]).Should().Be(Content(sent), "the sent version is untouched by the revision and its edits");
        (await CodeAsync(await PutAsync(support.Client, quote, 1, Body(RowVersion(sent), LineOf(variant, 5, 1_000))), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_NOT_EDITABLE");

        await using var db = _kit.Db();
        var q = await LoadAsync(db, quote);
        QuoteContentHash.Matches(q, q.Version(1), q.Version(1).ContentHash).Should().BeTrue("the sent hash, ship-to included, still verifies");
        q.Version(1).ShipTo!.SiteName.Should().Be("Revise plant");
    }

    [PostgresFact]
    public async Task PricePreview_ShowsTotalsTheBuyersOwnPriceAndTheFlagsASendWouldRaise_AndSavesNothing_CostOnlyWithPricingViewCost()
    {
        var (support, admin) = (await _kit.Rfq.PersonAsync(Permissions.Roles.Support), await _kit.Rfq.PersonAsync(Permissions.Roles.Admin));
        await TenPercentGlobalFloorAsync();
        var w = await CompanyAsync("Draft Preview Ltd");
        var variant = await VariantAsync();
        var net = await NetPriceAsync(variant);
        var contract = net * 8 / 10;   // the company's own price: 20 % below the public one
        const long cost = 50_000;      // floor = cost + 10 % = 55,000
        await SqlAsync($"UPDATE product_variants SET cost_price_minor = {cost} WHERE id = {variant}");
        await using (var db = _kit.Db())
        {
            var at = PriceListTestData.WholeSecond(DateTime.UtcNow.AddMinutes(-5));
            await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, w.Company, at, at.AddDays(30), at, new PriceListTestData.Entry(variant, 1, contract));
        }

        contract.Should().BeGreaterThan(60_000, "the precondition: the contract price is above the floor");
        var quote = await CreateAsync(support.Client, new { companyId = w.Company, contactUserId = w.Buyer });
        var before = await VersionAsync(support.Client, quote);
        async Task<int> AuditRowsAsync()
        {
            await using var db = _kit.Db();
            return await db.AuditLogs.CountAsync(a => a.EntityId == quote.ToString());
        }

        var auditBefore = await AuditRowsAsync();
        async Task<JsonElement> Preview(HttpClient who, object body) => await DataAsync(await PreviewAsync(who, quote, 1, body));

        // At the company's own (contract) price: within authority although it is 20 % below the public price, so a send would send.
        var atContract = await Preview(support.Client, Body(null, LineOf(variant, 3, contract)));
        atContract.GetProperty("outcome").GetString().Should().Be("send", "the discount is measured against the buyer's own price, not the public one");
        var line = atContract.GetProperty("lines")[0];
        (line.GetProperty("referenceUnitPriceMinor").GetInt64(), line.GetProperty("referenceSource").GetString(), line.GetProperty("publicUnitPriceMinor").GetInt64(),
            line.GetProperty("overAuthority").GetBoolean(), line.GetProperty("belowFloor").GetBoolean()).Should().Be((contract, "contract", net, false, false));
        (line.GetProperty("lineNetMinor").GetInt64(), atContract.GetProperty("goodsNetMinor").GetInt64()).Should().Be((3 * contract, 3 * contract));
        atContract.GetProperty("goodsTotalMinor").GetInt64().Should().Be(atContract.GetProperty("goodsNetMinor").GetInt64() + atContract.GetProperty("taxMinor").GetInt64());
        atContract.GetProperty("grandTotalMinor").ValueKind.Should().Be(JsonValueKind.Null, "calculated delivery is priced at acceptance");

        // Below the floor (and so far over the discount authority): refused until the line has a reason, then it needs approval.
        var low = await Preview(support.Client, Body(null, LineOf(variant, 3, 54_000)));
        (low.GetProperty("outcome").GetString(), low.GetProperty("problems").EnumerateArray().Single().GetString()).Should().Be(("refused", "BELOW_FLOOR_REASON_REQUIRED"));
        low.GetProperty("belowFloorLineNos").EnumerateArray().Select(n => n.GetInt32()).Should().Equal(1);
        low.GetProperty("overAuthorityLineNos").EnumerateArray().Select(n => n.GetInt32()).Should().Equal(1);
        low.GetProperty("reasonRequiredLineNos").EnumerateArray().Select(n => n.GetInt32()).Should().Equal(1);
        var reasoned = await Preview(support.Client, Body(null, LineOf(variant, 3, 54_000, reason: "Strategic account, owner agreed")));
        (reasoned.GetProperty("outcome").GetString(), reasoned.GetProperty("problems").GetArrayLength()).Should().Be(("approval", 0));

        // Below cost: a send would be refused whatever the reason.
        var belowCost = await Preview(support.Client, Body(null, LineOf(variant, 3, cost - 1, reason: "Clearance of old stock")));
        belowCost.GetProperty("problems").EnumerateArray().Select(p => p.GetString()).Should().Equal("QUOTE_BELOW_COST");
        belowCost.GetProperty("belowCostLineNos").EnumerateArray().Select(n => n.GetInt32()).Should().Equal(1);

        // Long validity alone needs a second person.
        var longValidity = await Preview(support.Client, new { validityDays = 31, lines = new[] { LineOf(variant, 3, contract) } });
        (longValidity.GetProperty("outcome").GetString(), longValidity.GetProperty("longValidity").GetBoolean()).Should().Be(("approval", true));

        // Cost figures only with pricing.view_cost: absent from Support's JSON, present for the Admin.
        var raw = await (await PreviewAsync(support.Client, quote, 1, Body(null, LineOf(variant, 3, 54_000)))).Content.ReadAsStringAsync();
        raw.Should().NotContain("\"cost\"").And.NotContain("costMinor").And.NotContain("floorMinor", "Support has no pricing.view_cost");
        raw.Should().Contain("\"referenceUnitPriceMinor\"").And.Contain("\"belowFloor\":true", "the flags and the buyer's price are for every editor");
        var adminLine = (await Preview(admin.Client, Body(null, LineOf(variant, 3, 54_000)))).GetProperty("lines")[0];
        (adminLine.GetProperty("cost").GetProperty("costMinorAtCheck").GetInt64(), adminLine.GetProperty("cost").GetProperty("floorMinorAtCheck").GetInt64()).Should().Be((cost, 55_000));

        Fingerprint(await VersionAsync(support.Client, quote)).Should().Be(Fingerprint(before), "a preview saves nothing");
        (await VersionAsync(support.Client, quote)).GetProperty("lines").GetArrayLength().Should().Be(0);
        (await AuditRowsAsync()).Should().Be(auditBefore, "a preview is not an edit");
    }
}
