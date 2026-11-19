using System.Net;
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
/// D4.6 over HTTP: the staff RFQ inbox and the quote lifecycle around drafts (ADR-0011 §2, §3, §9, §12-§14; threat model QT1, QT3, QT6).
/// Real host, real staff sign-ins with authenticator codes; buyers never sign in. Its own class, so its own login budget.
/// </summary>
public sealed class StaffQuoteEndpointsTests(RfqApiFactory factory) : IClassFixture<RfqApiFactory>
{
    private readonly StaffQuoteKit _kit = new(factory);
    private RfqApiKit.Person? _support, _finance, _admin;

    private async Task<RfqApiKit.Person> SupportAsync() => _support ??= await _kit.Rfq.PersonAsync(Permissions.Roles.Support);
    private async Task<RfqApiKit.Person> FinanceAsync() => _finance ??= await _kit.Rfq.PersonAsync(Permissions.Roles.Finance);
    private async Task<RfqApiKit.Person> AdminAsync() => _admin ??= await _kit.Rfq.PersonAsync(Permissions.Roles.Admin);

    private async Task<(Guid Company, Guid Owner, Guid Requester, Guid Buyer)> CompanyAsync(string name)
    {
        var (owner, requester, buyer) = (await _kit.BuyerAsync(), await _kit.BuyerAsync(), await _kit.BuyerAsync());
        var company = await _kit.Rfq.CompanyAsync(owner, name, true, (requester, CompanyRoles.Requester), (buyer, CompanyRoles.Buyer));
        return (company, owner, requester, buyer);
    }

    private async Task<Guid> VariantAsync() => (await _kit.Rfq.VariantAsync(null)).VariantId;

    /// <summary>What the order path charges the company per unit, net: no price list, so the catalogue price on the net basis.</summary>
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

    [PostgresFact]
    public async Task CreateFromAnRfq_PrefillsTheMatchedLines_AtTheCompanysOwnPrice_AndMovesTheRfqToQuoting()
    {
        var support = await SupportAsync();
        var (company, _, requester, _) = await CompanyAsync("Desk Prefill Ltd");
        var (v1, v2) = (await VariantAsync(), await VariantAsync());
        var rfq = await _kit.RfqAsync(company, requester, (v1, 3), (null, 5), (v2, 2));
        const long contract = 100_000;   // the company's approved contract price for v1, below its public net price
        await using (var db = _kit.Db())
        {
            var at = PriceListTestData.WholeSecond(DateTime.UtcNow.AddMinutes(-5));
            await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, company, at, at.AddDays(30), at, new PriceListTestData.Entry(v1, 1, contract));
        }

        (await NetPriceAsync(v1)).Should().BeGreaterThan(contract, "the precondition: the contract is the buyer's own, lower price");

        var created = await DataAsync(await PostAsync(support.Client, "/api/v1/admin/quotes", new { rfqId = rfq }), HttpStatusCode.Created);
        var quote = created.GetProperty("quote");
        (quote.GetProperty("status").GetString(), quote.GetProperty("rfqId").GetGuid(), quote.GetProperty("contactUserId").GetGuid(),
            quote.GetProperty("assignedTo").GetGuid(), quote.GetProperty("latestVersionNo").GetInt32())
            .Should().Be(("draft", rfq, requester, support.Id, 1), "the requester is the default contact and the creator holds it");
        var lines = created.GetProperty("versions")[0].GetProperty("lines").EnumerateArray().ToList();
        lines.Select(l => (l.GetProperty("variantId").GetGuid(), l.GetProperty("quantity").GetInt32(), l.GetProperty("unitPriceMinor").GetInt64()))
            .Should().Equal((v1, 3, contract), (v2, 2, await NetPriceAsync(v2)));
        lines[0].GetProperty("publicUnitPriceMinor").GetInt64().Should().Be(await NetPriceAsync(v1), "the snapshot keeps the public price beside it");
        lines.Should().OnlyContain(l => l.GetProperty("rfqLineId").ValueKind == JsonValueKind.String, "each line remembers the asked line");

        await using (var db = _kit.Db())
        {
            (await db.Rfqs.SingleAsync(r => r.Id == rfq)).Status.Should().Be(RfqStatus.Quoting);
            var id = quote.GetProperty("id").GetString();
            (await db.AuditLogs.CountAsync(a => a.Action == "quote.created" && a.EntityId == id && a.ActorId == support.Id)).Should().Be(1);
        }

        // A second quote from the same RFQ gets no line twice (a BOM above 50 lines becomes several quotes); after the first is closed, the
        // lines are free again.
        var second = await DataAsync(await PostAsync(support.Client, "/api/v1/admin/quotes", new { rfqId = rfq }), HttpStatusCode.Created);
        second.GetProperty("versions")[0].GetProperty("lines").GetArrayLength().Should().Be(0);
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote.GetProperty("id").GetGuid()}/close", new { reason = "Wrong split" }));
        var third = await DataAsync(await PostAsync(support.Client, "/api/v1/admin/quotes", new { rfqId = rfq }), HttpStatusCode.Created);
        third.GetProperty("versions")[0].GetProperty("lines").GetArrayLength().Should().Be(2);

        // The RFQ detail lists its quotes with their computed status.
        var detail = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs/{rfq}"));
        detail.GetProperty("quotes").EnumerateArray().Select(q => q.GetProperty("status").GetString()).Should().Equal("closed", "draft", "draft");
        detail.GetProperty("lines").GetArrayLength().Should().Be(3);
    }

    [PostgresFact]
    public async Task AQuoteContact_MustBeAMemberOfTheQuotesCompany_WithTheBuyerOrAdminRole()
    {
        var support = await SupportAsync();
        var (company, owner, requester, buyer) = await CompanyAsync("Desk Contact Ltd");
        var (other, _, _, otherBuyer) = await CompanyAsync("Desk Contact Other Ltd");
        const string url = "/api/v1/admin/quotes";

        await CodeAsync(await PostAsync(support.Client, url, new { companyId = company }), HttpStatusCode.BadRequest);   // no contact
        await CodeAsync(await PostAsync(support.Client, url, new { companyId = company, contactUserId = requester }), HttpStatusCode.BadRequest);
        await CodeAsync(await PostAsync(support.Client, url, new { companyId = company, contactUserId = otherBuyer }), HttpStatusCode.BadRequest);
        await CodeAsync(await PostAsync(support.Client, url, new { companyId = company, rfqId = Guid.NewGuid(), contactUserId = buyer }), HttpStatusCode.BadRequest);
        await CodeAsync(await PostAsync(support.Client, url, new { companyId = Guid.NewGuid(), contactUserId = buyer }), HttpStatusCode.NotFound);

        var pendingOwner = await _kit.BuyerAsync();
        var pending = await _kit.Rfq.CompanyAsync(pendingOwner, "Desk Pending Ltd", false);
        (await CodeAsync(await PostAsync(support.Client, url, new { companyId = pending, contactUserId = pendingOwner }), HttpStatusCode.Conflict))
            .Should().Be("COMPANY_NOT_ACTIVE");

        foreach (var contact in new[] { buyer, owner })
        {
            var created = await DataAsync(await PostAsync(support.Client, url, new { companyId = company, contactUserId = contact }), HttpStatusCode.Created);
            (created.GetProperty("quote").GetProperty("contactUserId").GetGuid(), created.GetProperty("versions")[0].GetProperty("lines").GetArrayLength())
                .Should().Be((contact, 0));
        }

        await using var db = _kit.Db();
        (await db.Quotes.CountAsync(q => q.CompanyId == company)).Should().Be(2, "the refused requests stored nothing");
        (await db.Quotes.CountAsync(q => q.CompanyId == other || q.CompanyId == pending)).Should().Be(0);
    }

    [PostgresFact]
    public async Task AStaffMemberWhoBelongsToTheCompany_CannotCreateOrReviseItsQuote()
    {
        var (insider, support) = (await _kit.Rfq.PersonAsync(Permissions.Roles.Support), await SupportAsync());
        var owner = await _kit.BuyerAsync();
        var company = await _kit.Rfq.CompanyAsync(owner, "Desk Insider Ltd", true, (insider.Id, CompanyRoles.Requester));
        var rfq = await _kit.RfqAsync(company, owner, (await VariantAsync(), 1));

        (await CodeAsync(await PostAsync(insider.Client, "/api/v1/admin/quotes", new { rfqId = rfq }), HttpStatusCode.Forbidden)).Should().Be("QUOTE_CONFLICT_OF_INTEREST");
        (await CodeAsync(await PostAsync(insider.Client, "/api/v1/admin/quotes", new { companyId = company, contactUserId = owner }), HttpStatusCode.Forbidden))
            .Should().Be("QUOTE_CONFLICT_OF_INTEREST");

        var quote = await CreateAsync(support.Client, new { rfqId = rfq });
        await _kit.SendAsync(quote);
        (await CodeAsync(await PostAsync(insider.Client, $"/api/v1/admin/quotes/{quote}/revise"), HttpStatusCode.Forbidden)).Should().Be("QUOTE_CONFLICT_OF_INTEREST");

        await using var db = _kit.Db();
        (await db.Quotes.CountAsync(q => q.CompanyId == company)).Should().Be(1, "only the outsider's quote exists");
        (await db.QuoteVersions.CountAsync(v => v.QuoteId == quote)).Should().Be(1, "the insider's revise made no version");
        (await db.Rfqs.SingleAsync(r => r.Id == rfq)).Status.Should().Be(RfqStatus.Quoting, "moved by the outsider only");

        // The insider still reads (the desk view is not an edit), and another company's quotes stay open to them.
        await DataAsync(await insider.Client.GetAsync($"/api/v1/admin/quotes/{quote}"));
    }

    [PostgresFact]
    public async Task Revise_MakesANewVersion_AndLeavesTheSentOneUntouched()
    {
        var support = await SupportAsync();
        var (company, _, requester, buyer) = await CompanyAsync("Desk Revise Ltd");
        var rfq = await _kit.RfqAsync(company, requester, (await VariantAsync(), 4), (await VariantAsync(), 1));
        var quote = await CreateAsync(support.Client, new { rfqId = rfq });
        await _kit.SendAsync(quote);
        string Fingerprint(JsonElement v) => v.GetProperty("contentHash").GetString() + "|" + string.Join(",", v.GetProperty("lines").EnumerateArray()
            .Select(l => $"{l.GetProperty("id")}:{l.GetProperty("variantId")}:{l.GetProperty("quantity")}:{l.GetProperty("unitPriceMinor")}"));
        var before = (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/quotes/{quote}"))).GetProperty("versions")[0];

        var revised = await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        var versions = revised.GetProperty("versions").EnumerateArray().ToList();
        versions.Select(v => (v.GetProperty("versionNo").GetInt32(), v.GetProperty("status").GetString())).Should().Equal((1, "sent"), (2, "draft"));
        Fingerprint(versions[0]).Should().Be(Fingerprint(before), "the sent version is untouched");
        versions[1].GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("variantId").GetGuid(), l.GetProperty("unitPriceMinor").GetInt64()))
            .Should().Equal(versions[0].GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("variantId").GetGuid(), l.GetProperty("unitPriceMinor").GetInt64())));
        versions[1].GetProperty("contributors").EnumerateArray().Select(c => c.GetGuid()).Should().Equal(support.Id);
        revised.GetProperty("quote").GetProperty("status").GetString().Should().Be("draft");

        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"), HttpStatusCode.Conflict)).Should().Be("QUOTE_OPEN_VERSION_EXISTS");
        await using (var db = _kit.Db())
            (await db.AuditLogs.CountAsync(a => a.Action == "quote.revised" && a.EntityId == quote.ToString())).Should().Be(1);

        // An accepted version with a live order blocks a revision; once that order lapsed before confirmation, revising is allowed.
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/close", new { reason = "Start again" }));
        var accepted = await CreateAsync(support.Client, new { rfqId = await _kit.RfqAsync(company, requester, (await VariantAsync(), 1)) });
        await _kit.SendAsync(accepted);
        Guid order;
        await using (var db = _kit.Db())
        {
            order = await CreditTestData.InsertOrderAsync(db, buyer, company, 1_000, onCredit: false, status: "PendingPayment");
            var q = await LoadAsync(db, accepted);
            q.Accept(1, q.Version(1).ContentHash!, order, buyer, false, DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{accepted}/revise"), HttpStatusCode.Conflict)).Should().Be("QUOTE_ALREADY_ACCEPTED");
        await using (var db = _kit.Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET status = 'Cancelled' WHERE id = {order}");
        var again = await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{accepted}/revise"));
        again.GetProperty("versions").GetArrayLength().Should().Be(2, "the order lapsed before confirmation, so the quote may be revised");
    }

    [PostgresFact]
    public async Task ClosingAQuote_WithdrawsItsLiveVersion_InOneTransaction()
    {
        var support = await SupportAsync();
        var (company, _, requester, _) = await CompanyAsync("Desk Close Ltd");
        var quote = await CreateAsync(support.Client, new { rfqId = await _kit.RfqAsync(company, requester, (await VariantAsync(), 2)) });
        await _kit.SendAsync(quote);

        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/close", new { reason = "no" }), HttpStatusCode.BadRequest)).Should().NotBeEmpty();
        await using (var db = _kit.Db())
            (await db.QuoteVersions.SingleAsync(v => v.QuoteId == quote)).Status.Should().Be(QuoteVersionStatus.Sent, "a refused close changes nothing");

        var closed = await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/close", new { reason = "Customer bought elsewhere" }));
        closed.GetProperty("quote").GetProperty("status").GetString().Should().Be("closed");
        closed.GetProperty("versions")[0].GetProperty("status").GetString().Should().Be("withdrawn");

        await using (var db = _kit.Db())
        {
            var q = await LoadAsync(db, quote);
            (q.ClosedBy, q.CloseReason).Should().Be((support.Id, "Customer bought elsewhere"));
            q.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Withdrawn && v.WithdrawnBy == support.Id
                && v.WithdrawReason == "Customer bought elsewhere" && v.WithdrawnAtUtc == q.ClosedAtUtc, "withdrawn in the same step as the close");
            q.LiveVersion.Should().BeNull("closing leaves no live version");
            (await db.OutboxMessages.CountAsync(m => m.EventType == nameof(QuoteWithdrawnEvent) && m.AggregateId == quote)).Should().Be(1);
            (await db.AuditLogs.CountAsync(a => a.Action == "quote.closed" && a.EntityId == quote.ToString() && a.ActorId == support.Id)).Should().Be(1);
        }

        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/close", new { reason = "Customer bought elsewhere" }), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_CLOSED");
        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"), HttpStatusCode.Conflict)).Should().Be("QUOTE_CLOSED");
    }

    [PostgresFact]
    public async Task CostFigures_AreInTheJson_OnlyWithPricingViewCost()
    {
        var (support, finance, admin) = (await SupportAsync(), await FinanceAsync(), await AdminAsync());
        var (company, _, requester, _) = await CompanyAsync("Desk Cost Ltd");
        var quote = await CreateAsync(support.Client, new { rfqId = await _kit.RfqAsync(company, requester, (await VariantAsync(), 2)) });
        await _kit.SendAsync(quote);

        var raw = await (await support.Client.GetAsync($"/api/v1/admin/quotes/{quote}")).Content.ReadAsStringAsync();
        raw.Should().NotContain("\"cost\"").And.NotContain("costMinorAtCheck").And.NotContain("floorMinorAtCheck", "Support has no pricing.view_cost");
        raw.Should().Contain("\"referenceUnitPriceMinor\"").And.Contain("\"belowFloor\"", "the flags and the buyer's price are for every viewer");

        foreach (var who in new[] { finance, admin })
        {
            var line = (await DataAsync(await who.Client.GetAsync($"/api/v1/admin/quotes/{quote}"))).GetProperty("versions")[0].GetProperty("lines")[0];
            line.GetProperty("cost").GetProperty("costMinorAtCheck").GetInt64().Should().Be(1);
            line.GetProperty("cost").GetProperty("floorMinorAtCheck").ValueKind.Should().Be(JsonValueKind.Number);
        }
    }

    [PostgresFact]
    public async Task RfqInbox_ShowsTheSlaDueTime_Filters_Assigns_AndCloses()
    {
        var (support, finance) = (await SupportAsync(), await FinanceAsync());
        var (company, _, requester, buyer) = await CompanyAsync("Desk Inbox " + Guid.NewGuid().ToString("N")[..6] + " Ltd");
        var variant = await VariantAsync();
        var (first, second) = (await _kit.RfqAsync(company, requester, (variant, 1)), await _kit.RfqAsync(company, buyer, (variant, 2)));

        var open = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs?status=open&companyId={company}"));
        open.GetProperty("total").GetInt32().Should().Be(2);
        var items = open.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("id").GetGuid()).Should().Equal(first, second);   // the inbox: oldest waiting first
        foreach (var item in items)
        {
            var created = item.GetProperty("createdAt").GetDateTime().ToUniversalTime();
            item.GetProperty("slaDueAt").GetDateTime().ToUniversalTime().Should().Be(QuoteSla.DueAtUtc(created, 240, []));
            var waited = QuoteSla.WorkingMinutes(created, DateTime.UtcNow, []);
            item.GetProperty("slaWorkingMinutes").GetInt32().Should().BeInRange(waited - 1, waited, "the working time waited so far");
            (item.GetProperty("status").GetString(), item.GetProperty("companyName").GetString(), item.GetProperty("requesterName").GetString())
                .Should().Be(("submitted", items[0].GetProperty("companyName").GetString(), "Desk Buyer"));
        }

        var name = items[0].GetProperty("companyName").GetString()!;
        (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs?q={Uri.EscapeDataString(name[5..14])}"))).GetProperty("total").GetInt32().Should().Be(2);
        (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs?companyId={company}&assigned=me"))).GetProperty("total").GetInt32().Should().Be(0);

        // Assign: only to someone who prepares quotes (Support, Admin), never to Finance or a buyer; the closed RFQ takes nothing more.
        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/rfqs/{first}/assign", new { assigneeId = finance.Id }), HttpStatusCode.BadRequest)).Should().NotBeEmpty();
        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/rfqs/{first}/assign", new { assigneeId = buyer }), HttpStatusCode.BadRequest)).Should().NotBeEmpty();
        var assigned = await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/rfqs/{first}/assign", new { assigneeId = support.Id }));
        assigned.GetProperty("rfq").GetProperty("assignedTo").GetGuid().Should().Be(support.Id);
        var mine = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs?companyId={company}&assigned=me"));
        mine.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(first);
        (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs?companyId={company}&assigned=none"))).GetProperty("total").GetInt32().Should().Be(1);
        (await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/rfqs/{first}/assign", new { assigneeId = (Guid?)null })))
            .GetProperty("rfq").GetProperty("assignedTo").ValueKind.Should().Be(JsonValueKind.Null);

        var closed = await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/rfqs/{second}/close", new { reason = "No bid: discontinued range" }));
        (closed.GetProperty("rfq").GetProperty("status").GetString(), closed.GetProperty("rfq").GetProperty("closeReason").GetString())
            .Should().Be(("closed", "No bid: discontinued range"));
        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/rfqs/{second}/assign", new { assigneeId = support.Id }), HttpStatusCode.Conflict)).Should().Be("RFQ_INVALID_TRANSITION");
        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/rfqs/{second}/close", new { reason = "Again and again" }), HttpStatusCode.Conflict)).Should().Be("RFQ_INVALID_TRANSITION");
        (await CodeAsync(await PostAsync(support.Client, "/api/v1/admin/quotes", new { rfqId = second }), HttpStatusCode.Conflict)).Should().Be("RFQ_INVALID_TRANSITION");
        (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs?status=closed&companyId={company}"))).GetProperty("total").GetInt32().Should().Be(1);
        (await CodeAsync(await support.Client.GetAsync("/api/v1/admin/rfqs?status=nonsense"), HttpStatusCode.BadRequest)).Should().NotBeEmpty();
        (await CodeAsync(await support.Client.GetAsync($"/api/v1/admin/rfqs/{Guid.NewGuid()}"), HttpStatusCode.NotFound)).Should().NotBeEmpty();

        await using var db = _kit.Db();
        (await db.AuditLogs.CountAsync(a => a.Action == "rfq.assigned" && a.EntityId == first.ToString() && a.ActorId == support.Id)).Should().Be(2);
        (await db.AuditLogs.CountAsync(a => a.Action == "rfq.closed" && a.EntityId == second.ToString())).Should().Be(1);
    }

    [PostgresFact]
    public async Task QuoteList_ShowsTheComputedStatus_AndOfTheCreditOnlyTermsAndHold()
    {
        var support = await SupportAsync();
        var (company, _, requester, buyer) = await CompanyAsync("Desk List " + Guid.NewGuid().ToString("N")[..6] + " Ltd");
        var variant = await VariantAsync();
        Task<Guid> NewAsync() => CreateAsync(support.Client, new { rfqId = (Guid?)null, companyId = company, contactUserId = buyer });
        async Task<Guid> WithLineAsync() => await CreateAsync(support.Client, new { rfqId = await _kit.RfqAsync(company, requester, (variant, 1)) });

        var draft = await NewAsync();
        var sent = await WithLineAsync();
        await _kit.SendAsync(sent);
        var expired = await WithLineAsync();
        await _kit.SendAsync(expired, at: DateTime.UtcNow.AddDays(-40));
        var closed = await NewAsync();
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{closed}/close", new { reason = "Duplicate quote" }));

        var all = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/quotes?companyId={company}"));
        all.GetProperty("items").EnumerateArray().Select(i => (i.GetProperty("id").GetGuid(), i.GetProperty("status").GetString()))
            .Should().BeEquivalentTo(new[] { (draft, "draft"), (sent, "sent"), (expired, "expired"), (closed, "closed") });
        foreach (var (status, id) in new[] { ("draft", draft), ("sent", sent), ("expired", expired), ("closed", closed) })
            (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/quotes?companyId={company}&status={status}")))
                .GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(new[] { id }, status);
        (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/quotes?companyId={company}&status=pending_approval"))).GetProperty("total").GetInt32().Should().Be(0);
        (await DataAsync(await support.Client.GetAsync($"/api/v1/admin/quotes?companyId={company}&assigned=me"))).GetProperty("total").GetInt32().Should().Be(4);
        (await CodeAsync(await support.Client.GetAsync("/api/v1/admin/quotes?status=superseded"), HttpStatusCode.BadRequest)).Should().NotBeEmpty();

        var raw = await (await support.Client.GetAsync($"/api/v1/admin/quotes?companyId={company}")).Content.ReadAsStringAsync();
        var item = JsonDocument.Parse(raw).RootElement.GetProperty("data").GetProperty("items")[0];
        (item.GetProperty("creditTerms").GetString(), item.GetProperty("creditOnHold").GetBoolean()).Should().Be(("none", false));
        raw.Should().NotContainAny(["creditLimit", "creditUsed", "creditAvailable", "creditOnAccount"], "only the terms and the hold flag");
    }
}
