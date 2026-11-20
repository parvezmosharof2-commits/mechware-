using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.Infrastructure.Search;
using Mechware.IntegrationTests.Support;
using StackExchange.Redis;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.10 over HTTP: the buyer's quote list, detail, by-number lookup, accept preview and reject (ADR-0011 §8.4, §8.8, §12, §13; threat model
/// QT1, QT6, QT8). Real host and sign-ins (Support writes and sends; buyers read), each person signed in once for the class, which has its own
/// host. One 404 body for everything the caller may not see, on every route; only versions Sent or later; field-by-field DTOs; private,
/// never stored; quote prices never in a public cache or the search document.
/// </summary>
public sealed class BuyerQuoteEndpointsTests(QuoteIssueFactory factory) : IClassFixture<QuoteIssueFactory>
{
    private const string NotFound = "NOT_FOUND|Quote not found.";
    private readonly QuoteIssueKit _kit = new(factory);

    private Task<RfqApiKit.Person> PersonAsync(string name) => _kit.StaffAsync(Permissions.Roles.Buyer, name);

    private Task<RfqApiKit.Person> SupportAsync() => _kit.StaffAsync(Permissions.Roles.Support);

    private static string Url(Guid company, Guid? quote = null, string tail = "") => $"/api/v1/companies/{company}/quotes" + (quote is { } q ? $"/{q}" : "") + tail;

    private static string ByNumber(string number) => "/api/v1/quotes/by-number/" + Uri.EscapeDataString(number);

    /// <summary>A quote for <paramref name="w"/> by Support at the item's net price; sent unless <paramref name="send"/> is false. The contact is the world's buyer, or the RFQ's requester.</summary>
    private async Task<(Guid Id, string Number)> QuoteAsync(World w, Item item, bool send = true, Guid? rfqId = null, object? header = null, long? price = null)
    {
        var support = await SupportAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, price ?? item.Net)], rfqId: rfqId, header: header);
        if (send) await SentAsync(support.Client, quote, "sent");
        await using var db = _kit.Db();
        return (quote, await db.Quotes.Where(q => q.Id == quote).Select(q => q.Number).SingleAsync());
    }

    private async Task<World> WorldAsync(params (Guid User, CompanyRoles Roles)[] members)
    {
        await _kit.TenPercentFloorAsync();
        return await _kit.CompanyAsync("Buyer Quotes Ltd", members);
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient who, string url, object body) => who.PostAsJsonAsync(url, body);

    private static async Task<JsonElement> DetailAsync(HttpClient who, Guid company, Guid quote) => await StaffQuoteKit.DataAsync(await who.GetAsync(Url(company, quote)));

    /// <summary>The ids in the caller's list of <paramref name="company"/>, and its total.</summary>
    private static async Task<(List<Guid> Ids, int Total)> ListAsync(HttpClient who, Guid company, string query = "")
    {
        var data = await StaffQuoteKit.DataAsync(await who.GetAsync(Url(company) + query));
        return ([.. data.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid())], data.GetProperty("total").GetInt32());
    }

    /// <summary>What the buyer screens send: the version and its hash (and, to show they are ignored, a company id).</summary>
    private static object Body(int versionNo, string? hash, Guid? company = null, long? total = null) =>
        new { versionNo, contentHash = hash, expectedGrandTotalMinor = total ?? 1, payOnCredit = false, companyId = company, reason = "Too slow a delivery" };

    private async Task SqlAsync(FormattableString sql)
    {
        await using var db = _kit.Db();
        await db.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    private async Task<string?> HashAsync(Guid quote, int n = 1) => (await _kit.LoadAsync(quote)).Version(n).ContentHash;

    /// <summary>Every buyer route for one (caller, company, quote): detail, preview, accept, reject; each answer's code and message.</summary>
    private static async Task<List<string>> EveryRouteAsync(HttpClient who, Guid company, Guid quote, string? hash, Guid bodyCompany, string because)
    {
        var accept = new HttpRequestMessage(HttpMethod.Post, Url(company, quote, "/accept")) { Content = JsonContent.Create(Body(1, hash, bodyCompany)) };
        accept.Headers.Add("Idempotency-Key", "acc-" + Guid.NewGuid().ToString("N"));
        return
        [
            await RfqApiKit.NotFoundShapeAsync(await who.GetAsync(Url(company, quote)), because + " (detail)"),
            await RfqApiKit.NotFoundShapeAsync(await PostAsync(who, Url(company, quote, "/accept-preview"), Body(1, hash, bodyCompany)), because + " (preview)"),
            await RfqApiKit.NotFoundShapeAsync(await who.SendAsync(accept), because + " (accept)"),
            await RfqApiKit.NotFoundShapeAsync(await PostAsync(who, Url(company, quote, "/reject"), Body(1, hash, bodyCompany)), because + " (reject)"),
        ];
    }

    // ---- QT1: one 404 for everything the caller may not see ---------------------------------------------------------------

    [PostgresFact]
    public async Task Idor_QuotesOfAnotherCompany_Are404()
    {
        var (buyer, outsider, lone, requester) = (await PersonAsync("buyer-a"), await PersonAsync("outsider"), await PersonAsync("lone"), await PersonAsync("requester"));
        var support = await SupportAsync();
        var theirs = await _kit.CompanyAsync("Outsider Ltd", (outsider.Id, CompanyRoles.Admin | CompanyRoles.Buyer), (buyer.Id, CompanyRoles.Buyer));
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer), (requester.Id, CompanyRoles.Requester));
        var item = await _kit.ItemAsync();
        var (quote, number) = await QuoteAsync(w, item);
        var (draft, draftNumber) = await QuoteAsync(w, item, send: false);
        var hash = await HashAsync(quote);

        var answers = new List<string>();
        answers.AddRange(await EveryRouteAsync(outsider.Client, w.Company, quote, hash, theirs.Company, "another company's admin and buyer"));
        answers.AddRange(await EveryRouteAsync(outsider.Client, theirs.Company, quote, hash, w.Company, "under their own company, naming the quote's in the body"));
        answers.AddRange(await EveryRouteAsync(buyer.Client, theirs.Company, quote, hash, w.Company, "a member of both companies, under the other one"));
        answers.AddRange(await EveryRouteAsync(lone.Client, w.Company, quote, hash, w.Company, "a buyer of no company"));
        answers.AddRange(await EveryRouteAsync(support.Client, w.Company, quote, hash, w.Company, "platform staff on a buyer route"));
        answers.AddRange(await EveryRouteAsync(requester.Client, w.Company, quote, hash, w.Company, "a Requester who is not the contact"));
        answers.AddRange(await EveryRouteAsync(buyer.Client, w.Company, Guid.NewGuid(), hash, w.Company, "an unknown quote"));
        answers.AddRange(await EveryRouteAsync(buyer.Client, w.Company, draft, hash, w.Company, "a quote that was never sent"));
        foreach (var who in new[] { outsider, lone, support, requester })
            answers.Add(await RfqApiKit.NotFoundShapeAsync(await who.Client.GetAsync(ByNumber(number)), "by-number, a caller who may not see it"));
        foreach (var who in new[] { outsider, lone, support })
            answers.Add(await RfqApiKit.NotFoundShapeAsync(await who.Client.GetAsync(Url(w.Company)), "the list of a company the caller is no member of"));
        answers.Add(await RfqApiKit.NotFoundShapeAsync(await buyer.Client.GetAsync(ByNumber(draftNumber)), "by-number, a draft"));
        foreach (var status in new[] { "Pending", "Rejected" })
        {
            await SqlAsync($"UPDATE companies SET status = {status} WHERE id = {w.Company}");
            answers.AddRange(await EveryRouteAsync(buyer.Client, w.Company, quote, hash, w.Company, $"a {status} company"));
            answers.Add(await RfqApiKit.NotFoundShapeAsync(await buyer.Client.GetAsync(Url(w.Company)), $"the list of a {status} company"));
            answers.Add(await RfqApiKit.NotFoundShapeAsync(await buyer.Client.GetAsync(ByNumber(number)), $"by-number, a {status} company"));
        }
        await SqlAsync($"UPDATE companies SET status = 'Active' WHERE id = {w.Company}");

        answers.Distinct().Should().ContainSingle().Which.Should().Be(NotFound);
        (await factory.CreateClient().GetAsync(Url(w.Company))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await factory.CreateClient().GetAsync(ByNumber(number))).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await _kit.LoadAsync(quote)).Version(1).Status.Should().Be(QuoteVersionStatus.Sent, "no refused call changed anything");
        await using (var db = _kit.Db()) (await db.Orders.CountAsync(o => o.QuoteId == quote)).Should().Be(0);
        (await DetailAsync(buyer.Client, w.Company, quote)).GetProperty("quote").GetProperty("id").GetGuid().Should().Be(quote, "the buyer of the quote's company sees it");
    }

    [PostgresFact]
    public async Task QuoteRoleMatrix_RequesterSeesOnlyOwn_BuyerAndAdminSeeAll_OthersNotFound()
    {
        var (buyer, admin, requester, approver) = (await PersonAsync("buyer-a"), await PersonAsync("admin-only"), await PersonAsync("requester"), await PersonAsync("approver"));
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer), (admin.Id, CompanyRoles.Admin), (requester.Id, CompanyRoles.Requester), (approver.Id, CompanyRoles.Approver));
        var item = await _kit.ItemAsync();
        var own = await QuoteAsync(w, item, rfqId: await _kit.Desk.RfqAsync(w.Company, requester.Id, (item.Variant, 2)));   // contact: the requester
        var others = await QuoteAsync(w, item);                                                                              // contact: the world's buyer

        foreach (var who in new[] { buyer, admin })
        {
            (await ListAsync(who.Client, w.Company)).Should().BeEquivalentTo((new List<Guid> { own.Id, others.Id }, 2));
            foreach (var q in new[] { own, others })
            {
                (await DetailAsync(who.Client, w.Company, q.Id)).GetProperty("quote").GetProperty("id").GetGuid().Should().Be(q.Id);
                (await StaffQuoteKit.DataAsync(await who.Client.GetAsync(ByNumber(q.Number)))).GetProperty("quoteId").GetGuid().Should().Be(q.Id);
            }
        }

        (await ListAsync(requester.Client, w.Company)).Should().BeEquivalentTo((new List<Guid> { own.Id }, 1));
        var mine = await DetailAsync(requester.Client, w.Company, own.Id);
        mine.GetProperty("quote").GetProperty("contactIsMe").GetBoolean().Should().BeTrue();
        (await RfqApiKit.NotFoundShapeAsync(await requester.Client.GetAsync(Url(w.Company, others.Id)), "another member's quote")).Should().Be(NotFound);
        (await RfqApiKit.NotFoundShapeAsync(await requester.Client.GetAsync(ByNumber(others.Number)), "another member's number")).Should().Be(NotFound);

        (await ListAsync(approver.Client, w.Company)).Should().BeEquivalentTo((new List<Guid>(), 0), "an Approver is outside Quotes:VisibleTo and no contact");
        foreach (var q in new[] { own, others })
            (await RfqApiKit.NotFoundShapeAsync(await approver.Client.GetAsync(Url(w.Company, q.Id)), "an Approver")).Should().Be(NotFound);

        // The contact who is no Buyer sees and may reject the quote, but only a Buyer accepts (and so previews) it.
        var hash = await HashAsync(own.Id);
        (await StaffQuoteKit.CodeAsync(await PostAsync(requester.Client, Url(w.Company, own.Id, "/accept-preview"), Body(1, hash)), HttpStatusCode.Forbidden))
            .Should().Be("COMPANY_ROLE_REQUIRED");
        var rejected = await StaffQuoteKit.DataAsync(await PostAsync(requester.Client, Url(w.Company, own.Id, "/reject"), Body(1, hash)));
        rejected.GetProperty("versions")[0].GetProperty("status").GetString().Should().Be("rejected");
        (await RfqApiKit.NotFoundShapeAsync(await PostAsync(approver.Client, Url(w.Company, others.Id, "/reject"), Body(1, await HashAsync(others.Id))), "an Approver rejects"))
            .Should().Be(NotFound);
    }

    [PostgresFact]
    public async Task AMemberOfTwoCompanies_SeesEachCompanysQuotesOnlyUnderThatCompany()
    {
        var buyer = await PersonAsync("buyer-a");
        var (a, b) = (await WorldAsync((buyer.Id, CompanyRoles.Buyer)), await WorldAsync((buyer.Id, CompanyRoles.Buyer)));
        var item = await _kit.ItemAsync();
        var (qa, qb) = (await QuoteAsync(a, item), await QuoteAsync(b, item));

        (await ListAsync(buyer.Client, a.Company)).Should().BeEquivalentTo((new List<Guid> { qa.Id }, 1));
        (await ListAsync(buyer.Client, b.Company)).Should().BeEquivalentTo((new List<Guid> { qb.Id }, 1));
        (await RfqApiKit.NotFoundShapeAsync(await buyer.Client.GetAsync(Url(b.Company, qa.Id)), "company A's quote under company B")).Should().Be(NotFound);
        (await RfqApiKit.NotFoundShapeAsync(await PostAsync(buyer.Client, Url(b.Company, qa.Id, "/accept-preview"), Body(1, await HashAsync(qa.Id))), "preview under B"))
            .Should().Be(NotFound);
        var refA = await StaffQuoteKit.DataAsync(await buyer.Client.GetAsync(ByNumber(qa.Number.ToLowerInvariant())));
        (refA.GetProperty("companyId").GetGuid(), refA.GetProperty("quoteId").GetGuid(), refA.GetProperty("number").GetString()).Should().Be((a.Company, qa.Id, qa.Number));
        (await StaffQuoteKit.DataAsync(await buyer.Client.GetAsync(ByNumber(qb.Number)))).GetProperty("companyId").GetGuid().Should().Be(b.Company);
    }

    // ---- QT6: what a buyer gets ----------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task DraftsAndVersionsAwaitingApproval_NeverReachTheBuyer()
    {
        var buyer = await PersonAsync("buyer-a");
        var support = await SupportAsync();
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer));
        var item = await _kit.ItemAsync();
        var (quote, _) = await QuoteAsync(w, item);

        // Version 2 is drafted at a lower price, then waits for approval (over the discount authority); the buyer still sees only version 1.
        await StaffQuoteKit.DataAsync(await StaffQuoteKit.PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        await EditAsync(support.Client, quote, 2, [Line(item, item.Net * 9 / 10)]);
        foreach (var step in new[] { "draft", "pending" })
        {
            if (step == "pending") await SentAsync(support.Client, quote, "pending_approval", 2);
            var detail = await DetailAsync(buyer.Client, w.Company, quote);
            detail.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("versionNo").GetInt32()).Should().Equal([1], step);
            (detail.GetProperty("quote").GetProperty("versionNo").GetInt32(), detail.GetProperty("quote").GetProperty("status").GetString()).Should().Be((1, "sent"), step);
            detail.GetRawText().Should().NotContain((item.Net * 9 / 10 * 2).ToString(), step);
            (await StaffQuoteKit.DataAsync(await buyer.Client.GetAsync(Url(w.Company)))).GetProperty("items")[0].GetProperty("versionNo").GetInt32().Should().Be(1, step);
            (await RfqApiKit.NotFoundShapeAsync(await PostAsync(buyer.Client, Url(w.Company, quote, "/accept-preview"), Body(2, new string('a', 64))), step))
                .Should().Be(NotFound);
            (await RfqApiKit.NotFoundShapeAsync(await PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), new { versionNo = 2 }), step + " (reject)"))
                .Should().Be(NotFound, "a 409 would tell the buyer that a version 2 exists");
        }
    }

    [PostgresFact]
    public async Task QuoteLists_CountOnlyTheCallersVisibleQuotes()
    {
        var (buyer, requester) = (await PersonAsync("buyer-a"), await PersonAsync("requester"));
        var support = await SupportAsync();
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer), (requester.Id, CompanyRoles.Requester));
        var item = await _kit.ItemAsync();
        var first = await QuoteAsync(w, item);
        var theirs = await QuoteAsync(w, item, rfqId: await _kit.Desk.RfqAsync(w.Company, requester.Id, (item.Variant, 2)));
        await QuoteAsync(w, item, send: false);                                         // a draft
        var pending = await QuoteAsync(w, item, send: false, price: item.Net * 9 / 10);  // waiting for approval
        await SentAsync(support.Client, pending.Id, "pending_approval");
        // A second version of the first quote: still one quote.
        await StaffQuoteKit.DataAsync(await StaffQuoteKit.PostAsync(support.Client, $"/api/v1/admin/quotes/{first.Id}/revise"));
        await SentAsync(support.Client, first.Id, "sent", 2);
        // One sent 20 days ago for 15 days: expired now.
        (Guid Id, string Number) expired;
        try
        {
            factory.Clock.Offset = TimeSpan.FromDays(-20);
            expired = await QuoteAsync(w, item);
        }
        finally
        {
            factory.Clock.Offset = TimeSpan.Zero;
        }

        (await ListAsync(buyer.Client, w.Company)).Total.Should().Be(3);
        (await ListAsync(requester.Client, w.Company)).Should().BeEquivalentTo((new List<Guid> { theirs.Id }, 1));
        (await ListAsync(buyer.Client, w.Company, "?status=sent")).Total.Should().Be(2, "an expired version is not open");
        (await ListAsync(buyer.Client, w.Company, "?status=EXPIRED")).Should().BeEquivalentTo((new List<Guid> { expired.Id }, 1));
        (await DetailAsync(buyer.Client, w.Company, expired.Id)).GetProperty("quote").GetProperty("status").GetString().Should().Be("expired");
        (await ListAsync(buyer.Client, w.Company, "?status=superseded")).Total.Should().Be(0, "a quote is listed by its newest visible version");
        var page = await ListAsync(buyer.Client, w.Company, "?page=3&pageSize=1");
        (page.Ids.Count, page.Total).Should().Be((1, 3));
        (await StaffQuoteKit.CodeAsync(await buyer.Client.GetAsync(Url(w.Company) + "?status=draft"), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        var detail = await DetailAsync(buyer.Client, w.Company, first.Id);
        detail.GetProperty("versions").EnumerateArray().Select(v => v.GetProperty("status").GetString()).Should().Equal("superseded", "sent");
        (detail.GetProperty("versions")[0].GetProperty("acceptable").GetBoolean(), detail.GetProperty("versions")[1].GetProperty("acceptable").GetBoolean())
            .Should().Be((false, true));
    }

    private static readonly HashSet<string> AllowedJson =
    [
        "id", "number", "rfqId", "versionNo", "status", "sentAt", "validUntil", "lineCount", "goodsTotalMinor", "grandTotalMinor", "paymentTerms",
        "shippingMode", "contactIsMe", "closedAt", "items", "page", "pageSize", "total", "quote", "versions", "acceptable", "validityDays", "shipToSiteId",
        "shipTo", "shippingFees", "termsText", "buyerNote", "company", "preparerName", "contentHash", "goodsNetMinor", "taxMinor", "shippingTotalMinor",
        "acceptedOrderId", "acceptedAt", "rejectedAt", "rejectReason", "withdrawnAt", "supersededAt", "lines", "lineNo", "variantId", "sku", "partNumber",
        "titleEn", "titleBn", "quantity", "unitPriceMinor", "vatRate", "lineNetMinor", "lineTaxMinor", "lineTotalMinor", "lineNote", "name", "bin",
        "billingAddress", "quoteId", "subtotalMinor", "shippingMinor", "vatRateChanged", "lowerPriceNow", "stockShort", "quoteLineId", "title",
        "netMinor", "totalMinor", "data", "companyId", "sellerId", "siteName", "recipientName", "phone", "line1", "line2", "upazila", "district",
        "division", "postalCode",
    ];

    private static IEnumerable<string> PropertyNames(JsonElement e) => e.ValueKind switch
    {
        JsonValueKind.Object => e.EnumerateObject().SelectMany(p => PropertyNames(p.Value).Prepend(p.Name)),
        JsonValueKind.Array => e.EnumerateArray().SelectMany(PropertyNames),
        _ => [],
    };

    [PostgresFact]
    public async Task BuyerQuoteJson_CarriesNoCostFlagsOrStaffNotes()
    {
        var (buyer, support) = (await PersonAsync("buyer-a"), await SupportAsync());
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer));
        var item = await _kit.ItemAsync(costPercent: 50);
        var (quote, number) = await QuoteAsync(w, item, header: new { internalNote = "INTERNAL-ONLY-7731", buyerNote = "Thank you", termsText = "Net 30 days" });

        var bodies = new[]
        {
            await (await buyer.Client.GetAsync(Url(w.Company))).Content.ReadAsStringAsync(),
            await (await buyer.Client.GetAsync(Url(w.Company, quote))).Content.ReadAsStringAsync(),
            await (await buyer.Client.GetAsync(ByNumber(number))).Content.ReadAsStringAsync(),
            await (await PostAsync(buyer.Client, Url(w.Company, quote, "/accept-preview"), Body(1, await HashAsync(quote)))).Content.ReadAsStringAsync(),
        };
        foreach (var text in bodies)
        {
            using var doc = JsonDocument.Parse(text);
            PropertyNames(doc.RootElement).Distinct().Should().BeSubsetOf(AllowedJson, text);
            text.Should().NotContain("INTERNAL-ONLY-7731").And.NotContain(item.Cost.ToString()).And.NotContain(support.Id.ToString()).And.NotContainAny("belowFloor", "internalNote", "flags");
        }
        bodies[1].Should().Contain("Thank you").And.Contain("Net 30 days");
    }

    [PostgresFact]
    public async Task QuoteEndpoints_SendPrivateNoStore()
    {
        var buyer = await PersonAsync("buyer-a");
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer));
        var item = await _kit.ItemAsync();
        var (quote, number) = await QuoteAsync(w, item);
        var hash = await HashAsync(quote);

        var responses = new List<HttpResponseMessage>
        {
            await buyer.Client.GetAsync(Url(w.Company)),
            await buyer.Client.GetAsync(Url(w.Company) + "?status=nope"),
            await buyer.Client.GetAsync(Url(w.Company, quote)),
            await buyer.Client.GetAsync(Url(w.Company, Guid.NewGuid())),
            await buyer.Client.GetAsync(ByNumber(number)),
            await buyer.Client.GetAsync(ByNumber("nope")),
            await PostAsync(buyer.Client, Url(w.Company, quote, "/accept-preview"), Body(1, hash)),
            await PostAsync(buyer.Client, Url(w.Company, quote, "/accept-preview"), new { versionNo = 1 }),
            await PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), new { reason = "no version" }),
            await PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), Body(1, hash)),
            await PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), Body(1, hash)),
        };
        responses.Select(r => (int)r.StatusCode).Should().Equal(200, 400, 200, 404, 200, 404, 200, 400, 400, 200, 409);
        foreach (var r in responses) RfqApiKit.ShouldBePrivate(r);
    }

    [PostgresAndRedisFact]
    public async Task QuotePrices_NeverReachPublicCachesOrTheSearchIndex()
    {
        var buyer = await PersonAsync("buyer-a");
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer));
        var item = await _kit.ItemAsync();
        var price = item.Net - 37;   // within the discount authority, so sent at once
        var (quote, _) = await QuoteAsync(w, item, price: price);
        string[] quoted = [price.ToString(), (2 * price).ToString(), $"{price / 100}.{price % 100:D2}", $"{2 * price / 100}.{2 * price % 100:D2}"];
        string slug;
        Guid productId;
        await using (var db = _kit.Db())
        {
            productId = await db.ProductVariants.Where(v => v.Id == item.Variant).Select(v => v.ProductId).SingleAsync();
            slug = await db.Products.Where(p => p.Id == productId).Select(p => p.Slug).SingleAsync();
        }

        // The buyer reads the quote and previews it, then browses the public product page; a guest does too.
        (await buyer.Client.GetAsync(Url(w.Company, quote))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await PostAsync(buyer.Client, Url(w.Company, quote, "/accept-preview"), Body(1, await HashAsync(quote)))).StatusCode.Should().Be(HttpStatusCode.OK);
        var product = $"/api/v1/storefront/products/{slug}";
        var asBuyer = await (await buyer.Client.GetAsync(product)).Content.ReadAsStringAsync();
        var asGuest = await (await factory.CreateClient().GetAsync(product)).Content.ReadAsStringAsync();
        asBuyer.Should().Be(asGuest).And.Contain(slug);
        foreach (var n in quoted) asBuyer.Should().NotContain(n);

        // Every sf:* value about this product, whatever its type, and the search document the indexer would write now.
        using var redis = await ConnectionMultiplexer.ConnectAsync(TestInfra.RedisConnection);
        var cache = redis.GetDatabase();
        var seen = new List<string>();
        foreach (var endpoint in redis.GetEndPoints())
            foreach (var key in redis.GetServer(endpoint).Keys(pattern: "sf:*"))
            {
                var value = await cache.KeyTypeAsync(key) == RedisType.String ? (string?)await cache.StringGetAsync(key) ?? "" : "";
                if (!(key.ToString().Contains(slug) || value.Contains(slug) || value.Contains(item.Variant.ToString()))) continue;
                seen.Add(key!);
                foreach (var n in quoted) ($"{key} {value}").Should().NotContain(n, key);
            }
        seen.Should().Contain($"sf:product:v2:{slug}", "the product page was cached, so the scan saw it");
        await using var ctx = _kit.Db();
        var document = JsonSerializer.Serialize(await new ProductSearchDocumentLoader(ctx, TimeProvider.System).LoadAsync([productId]));
        document.Should().Contain(slug);
        foreach (var n in quoted) document.Should().NotContain(n);
    }

    // ---- QT8: by number --------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task QuoteByNumber_UnknownMalformedOrForeign_IsTheSame404()
    {
        var (buyer, outsider) = (await PersonAsync("buyer-a"), await PersonAsync("outsider"));
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer));
        await _kit.CompanyAsync("Outsider Ltd", (outsider.Id, CompanyRoles.Admin | CompanyRoles.Buyer));
        var (quote, number) = await QuoteAsync(w, await _kit.ItemAsync());

        var answers = new List<string>();
        foreach (var unknown in new[] { QuoteNumber.NewQuote(DateTime.UtcNow), QuoteNumber.NewRfq(DateTime.UtcNow), "nope", number[..10], number + "X", "MWQ-991399-ABCDEF" })
            answers.Add(await RfqApiKit.NotFoundShapeAsync(await buyer.Client.GetAsync(ByNumber(unknown)), unknown));
        answers.Add(await RfqApiKit.NotFoundShapeAsync(await outsider.Client.GetAsync(ByNumber(number)), "another company's number"));
        answers.Distinct().Should().ContainSingle().Which.Should().Be(NotFound);
        (await StaffQuoteKit.DataAsync(await buyer.Client.GetAsync(ByNumber(number)))).GetProperty("quoteId").GetGuid().Should().Be(quote);
    }

    // ---- reject (§8.8) -----------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Reject_WithWhatShouldChange_RejectsTheVersion_AuditsIt_AndQueuesQuoteRejected()
    {
        var buyer = await PersonAsync("buyer-a");
        var support = await SupportAsync();
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer));
        var (quote, _) = await QuoteAsync(w, await _kit.ItemAsync());
        var hash = await HashAsync(quote);

        (await StaffQuoteKit.CodeAsync(await PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), new { versionNo = 1, reason = new string('x', 1_001) }),
            HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await RfqApiKit.NotFoundShapeAsync(await PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), new { versionNo = 2 }), "no version 2")).Should().Be(NotFound);

        var data = await StaffQuoteKit.DataAsync(await PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), new { versionNo = 1, reason = "  Delivery in 5 days\u0007, please " }));
        var v = data.GetProperty("versions")[0];
        (v.GetProperty("status").GetString(), v.GetProperty("rejectReason").GetString(), v.GetProperty("acceptable").GetBoolean())
            .Should().Be(("rejected", "Delivery in 5 days, please", false));
        data.GetProperty("quote").GetProperty("status").GetString().Should().Be("rejected");

        var stored = (await _kit.LoadAsync(quote)).Version(1);
        (stored.Status, stored.RejectedBy, stored.RejectReason).Should().Be((QuoteVersionStatus.Rejected, buyer.Id, "Delivery in 5 days, please"));
        var audit = (await _kit.AuditAsync(quote, "quote.rejected")).Should().ContainSingle().Subject;
        (audit.ActorId, audit.ActorIp).Should().Be((buyer.Id, QuoteIssueFactory.ClientIp));
        audit.NewValuesJson.Should().Contain(hash!).And.Contain("Rejected");
        (await _kit.OutboxAsync(quote)).Should().Contain(nameof(QuoteRejectedEvent));

        // Once only, no acceptance after it, and the quote stays open for a revision.
        (await StaffQuoteKit.CodeAsync(await PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), new { versionNo = 1 }), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_INVALID_TRANSITION");
        (await StaffQuoteKit.CodeAsync(await PostAsync(buyer.Client, Url(w.Company, quote, "/accept-preview"), Body(1, hash)), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_NOT_ACCEPTABLE");
        (await StaffQuoteKit.PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task ARejectWaitsForTheQuoteLock_AndSeesAWithdrawalThatCommittedMeanwhile()
    {
        var buyer = await PersonAsync("buyer-a");
        var w = await WorldAsync((buyer.Id, CompanyRoles.Buyer));
        var (quote, _) = await QuoteAsync(w, await _kit.ItemAsync());

        // Staff hold the quote row and withdraw the version in a transaction that is still open when the buyer rejects.
        await using var staff = _kit.Db();
        await using var tx = await staff.Database.BeginTransactionAsync();
        await staff.Database.ExecuteSqlInterpolatedAsync($"SELECT id FROM quotes WHERE id = {quote} FOR NO KEY UPDATE");
        var held = await StaffQuoteKit.LoadAsync(staff, quote);
        held.Withdraw(1, Guid.NewGuid(), "Supplier price went up", DateTime.UtcNow);
        await staff.SaveChangesAsync();

        var reject = PostAsync(buyer.Client, Url(w.Company, quote, "/reject"), new { versionNo = 1 });
        await Task.Delay(1_500);
        reject.IsCompleted.Should().BeFalse("the reject waits for the quote row");
        await tx.CommitAsync();

        (await StaffQuoteKit.CodeAsync(await reject, HttpStatusCode.Conflict)).Should().Be("QUOTE_INVALID_TRANSITION");
        (await _kit.LoadAsync(quote)).Version(1).Status.Should().Be(QuoteVersionStatus.Withdrawn);
        (await _kit.AuditAsync(quote, "quote.rejected")).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task RejectRacingAnAccept_OneWins_AndNeverA500()
    {
        var (a, b) = (await PersonAsync("buyer-a"), await PersonAsync("buyer-b"));
        for (var round = 0; round < 4; round++)
        {
            var w = await WorldAsync((a.Id, CompanyRoles.Buyer), (b.Id, CompanyRoles.Buyer));
            await using (var db = _kit.Db())
                await new Infrastructure.Companies.CompanyService(db).AdminSetCreditAsync(w.Company, new Application.Companies.SetCreditRequest(CreditTerms.Net30, 100_000_000));
            var (quote, _) = await QuoteAsync(w, await _kit.ItemAsync());
            var stored = (await _kit.LoadAsync(quote)).Version(1);
            var accept = new HttpRequestMessage(HttpMethod.Post, Url(w.Company, quote, "/accept"))
            {
                Content = JsonContent.Create(new { versionNo = 1, contentHash = stored.ContentHash, expectedGrandTotalMinor = stored.GrandTotalMinor ?? stored.GoodsTotalMinor, payOnCredit = true }),
            };
            accept.Headers.Add("Idempotency-Key", "acc-" + Guid.NewGuid().ToString("N"));
            using var gate = new ManualResetEventSlim(false);
            var accepting = Task.Run(() => { gate.Wait(); return a.Client.SendAsync(accept); });
            var rejecting = Task.Run(() => { gate.Wait(); return PostAsync(b.Client, Url(w.Company, quote, "/reject"), new { versionNo = 1 }); });
            gate.Set();
            var (accepted, rejected) = ((await accepting).StatusCode, (await rejecting).StatusCode);
            new[] { accepted, rejected }.Should().BeEquivalentTo(accepted == HttpStatusCode.Created
                ? [HttpStatusCode.Created, HttpStatusCode.Conflict] : new[] { HttpStatusCode.Conflict, HttpStatusCode.OK }, $"round {round}");
            (await _kit.LoadAsync(quote)).Version(1).Status.Should().Be(accepted == HttpStatusCode.Created ? QuoteVersionStatus.Accepted : QuoteVersionStatus.Rejected);
        }
    }
}

/// <summary>A host whose <c>QuoteRead</c> budget is 3 a minute, for the one test that wants to exhaust it.</summary>
public sealed class QuoteReadLimitFactory() : DbBackedFactory(null, new Dictionary<string, string> { ["RateLimiting:QuoteReadPermitPerMinute"] = "3" });

/// <summary>D4.10, threat model QT8: the by-number lookup is limited per signed-in user (<c>QuoteRead</c>); lists and details are not.</summary>
public sealed class QuoteByNumberRateLimitTests(QuoteReadLimitFactory factory) : IClassFixture<QuoteReadLimitFactory>
{
    [PostgresFact]
    public async Task QuoteByNumber_IsRateLimited()
    {
        var kit = new RfqApiKit(factory);
        var (buyer, other) = (await kit.PersonAsync(), await kit.PersonAsync());
        var company = await kit.CompanyAsync(buyer.Id, "Limit Ltd " + Guid.NewGuid().ToString("N")[..6], true);
        var url = "/api/v1/quotes/by-number/" + QuoteNumber.NewQuote(DateTime.UtcNow);

        for (var i = 0; i < 3; i++) (await buyer.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var limited = await buyer.Client.GetAsync(url);
        limited.StatusCode.Should().Be(HttpStatusCode.TooManyRequests);
        (await other.Client.GetAsync(url)).StatusCode.Should().Be(HttpStatusCode.NotFound, "the budget is per user");
        (await buyer.Client.GetAsync($"/api/v1/companies/{company}/quotes")).StatusCode.Should().Be(HttpStatusCode.OK, "the list is not under QuoteRead");
    }
}
