using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;
using static Mechware.IntegrationTests.Quotes.StaffQuoteKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.8 over HTTP, the send side (ADR-0011 §3, §4, §5, §7, §8.3, §9, §14; threat model QT3, QT5, QT6, QT7, QT16): send with the flags the
/// server computes now, supersede and RFQ answered in one transaction, validity from the instant it is sent, the ship-to site read again,
/// recall, reject approval and withdraw. Real host, real staff sign-ins; buyers never sign in. Its own class, so its own login budget.
/// </summary>
public sealed class QuoteSendEndpointsTests(QuoteIssueFactory factory) : IClassFixture<QuoteIssueFactory>
{
    private readonly QuoteIssueKit _kit = new(factory);

    private async Task SqlAsync(FormattableString sql)
    {
        await using var db = _kit.Db();
        await db.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    private async Task<Guid> SiteAsync(Guid company, string name)
    {
        await using var db = _kit.Db();
        var site = new CompanySite(Guid.NewGuid(), company, name, "Karim Mia", "01711000000", "12 Tejgaon I/A", "Gate 2", "Tejgaon", "Dhaka", "Dhaka", "1208", false);
        db.CompanySites.Add(site);
        await db.SaveChangesAsync();
        return site.Id;
    }

    private async Task<Guid> SellerOfAsync(Guid variant)
    {
        await using var db = _kit.Db();
        return await (from v in db.ProductVariants join p in db.Products on v.ProductId equals p.Id where v.Id == variant select p.SellerId).SingleAsync();
    }

    private static Guid LineId(JsonElement version, int lineNo = 1) => version.GetProperty("lines")[lineNo - 1].GetProperty("id").GetGuid();

    [PostgresFact]
    public async Task ABelowFloorLine_CannotBeSentWithoutApproval()
    {
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Floor Ltd");
        var item = await _kit.ItemAsync(costPercent: 50);   // floor = 55 % of the net price
        var rfq = await _kit.Desk.RfqAsync(w.Company, w.Buyer, (item.Variant, 2));
        var low = item.Net * 52 / 100;
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, low)], rfqId: rfq);

        (await CodeAsync(await SendAsync(support.Client, quote), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR", "a below-floor line needs its reason");
        (await VersionAsync(support.Client, quote)).GetProperty("status").GetString().Should().Be("draft");

        await EditAsync(support.Client, quote, 1, [Line(item, low, reason: "Strategic account, owner agreed")]);
        var sent = await SentAsync(support.Client, quote, "pending_approval");
        var v = sent.GetProperty("quote").GetProperty("versions")[0];
        Ids(sent.GetProperty("flags").GetProperty("belowFloorLineIds")).Should().Equal(LineId(v));
        Ids(sent.GetProperty("flags").GetProperty("overAuthorityLineIds")).Should().Equal(new[] { LineId(v) }, "52 % of the buyer's price is also far over the 5 % authority");
        (v.GetProperty("status").GetString(), v.GetProperty("sentAt").ValueKind, v.GetProperty("validUntil").ValueKind, v.GetProperty("submittedBy").GetGuid())
            .Should().Be(("pending_approval", JsonValueKind.Null, JsonValueKind.Null, support.Id), "nothing is offered to the buyer before the approval");
        v.GetProperty("lines")[0].GetProperty("belowFloor").GetBoolean().Should().BeTrue();

        (await CodeAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/versions/1/price-preview", new { validityDays = 15 }), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_NOT_EDITABLE", "the version is frozen while it waits");
        var stored = await _kit.LoadAsync(quote);
        QuoteContentHash.Matches(stored, stored.Version(1), stored.Version(1).ContentHash).Should().BeTrue("the hash the approver will see is stored");
        (await _kit.RfqOfAsync(rfq)).Status.Should().Be(RfqStatus.Quoting, "a request is answered when a quote is sent, not when it waits");
        (await _kit.OutboxAsync(quote)).Should().Equal(nameof(QuoteApprovalRequestedEvent));
        (await _kit.AuditAsync(quote, "quote.submitted_for_approval")).Should().ContainSingle().Which.ActorId.Should().Be(support.Id);

        // The approval (below the floor: an Admin) is the send: now the buyer is offered it and the request is answered.
        (await _kit.ApproveAsync(await _kit.StaffAsync(Permissions.Roles.Admin), quote)).StatusCode.Should().Be(HttpStatusCode.OK);
        var approved = (await _kit.LoadAsync(quote)).Version(1);
        var answered = await _kit.RfqOfAsync(rfq);
        (approved.Status, answered.Status, answered.FirstQuoteSentAtUtc).Should().Be((QuoteVersionStatus.Sent, RfqStatus.Answered, approved.SentAtUtc));
        (await _kit.OutboxAsync(quote)).Should().Equal(nameof(QuoteApprovalRequestedEvent), nameof(QuoteSentEvent));
    }

    [PostgresFact]
    public async Task ASendWithoutAFlag_IsSentNow_SupersedesTheLiveVersion_AndAnswersTheRfq()
    {
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Clean Ltd");
        var item = await _kit.ItemAsync();
        var rfq = await _kit.Desk.RfqAsync(w.Company, w.Buyer, (item.Variant, 2));
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)], rfqId: rfq);

        var before = DateTime.UtcNow;
        var sent = await SentAsync(support.Client, quote, "sent");
        (Ids(sent.GetProperty("flags").GetProperty("belowFloorLineIds")).Count, Ids(sent.GetProperty("flags").GetProperty("overAuthorityLineIds")).Count,
            sent.GetProperty("flags").GetProperty("longValidity").GetBoolean()).Should().Be((0, 0, false));
        var stored = await _kit.LoadAsync(quote);
        var v1 = stored.Version(1);
        (v1.Status, v1.SentBy, v1.SubmittedBy, v1.PreparerName).Should().Be((QuoteVersionStatus.Sent, support.Id, support.Id, "Rfq support"));
        v1.SentAtUtc.Should().BeOnOrAfter(before.AddSeconds(-1));
        v1.ValidUntilUtc.Should().Be(QuoteValidity.ValidUntilUtc(v1.SentAtUtc!.Value, 15));
        QuoteContentHash.Matches(stored, v1, v1.ContentHash).Should().BeTrue();
        await using (var db = _kit.Db())
        {
            var company = await db.Companies.AsNoTracking().SingleAsync(c => c.Id == w.Company);
            (v1.CompanySnapshot!.Name, v1.CompanySnapshot.Bin).Should().Be((company.LegalName, company.Bin), "the buyer company is frozen with the version");
        }

        var answered = await _kit.RfqOfAsync(rfq);
        (answered.Status, answered.FirstQuoteSentAtUtc).Should().Be((RfqStatus.Answered, v1.SentAtUtc));

        // A newer version supersedes the live one in the same step; the request keeps its first answer time.
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        await EditAsync(support.Client, quote, 2, [Line(item, item.Net - 1, quantity: 3)]);
        await SentAsync(support.Client, quote, "sent", 2);
        stored = await _kit.LoadAsync(quote);
        (stored.Version(1).Status, stored.Version(2).Status).Should().Be((QuoteVersionStatus.Superseded, QuoteVersionStatus.Sent));
        stored.Version(1).SupersededAtUtc.Should().Be(stored.Version(2).SentAtUtc, "one instant, one transaction");
        (await _kit.RfqOfAsync(rfq)).FirstQuoteSentAtUtc.Should().Be(v1.SentAtUtc);
        (await _kit.OutboxAsync(quote)).Should().Equal(nameof(QuoteSentEvent), nameof(QuoteSentEvent));
        var audit = await _kit.AuditAsync(quote, "quote.sent");
        audit.Should().HaveCount(2);
        using (var row = JsonDocument.Parse(audit[1].NewValuesJson!))
            (row.RootElement.GetProperty("SupersededVersionNo").GetInt32(), row.RootElement.GetProperty("ContentHash").GetString())
                .Should().Be((1, stored.Version(2).ContentHash));
    }

    [PostgresFact]
    public async Task ASendIsOneTransaction_AFailureAtCommitLeavesTheLiveVersionAndEverythingElseAsItWas()
    {
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Atomic Ltd");
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        await SentAsync(support.Client, quote, "sent");
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        await EditAsync(support.Client, quote, 2, [Line(item, item.Net, quantity: 4)]);
        var (auditBefore, outboxBefore) = ((await _kit.AuditAsync(quote)).Count, (await _kit.OutboxAsync(quote)).Count);

        // The last write of the send (its outbox event) fails: the supersede, the send and the audit row must all be undone with it.
        await using (var db = _kit.Db())
        {
            await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION d48_injected_failure() RETURNS trigger LANGUAGE plpgsql AS 'BEGIN RAISE EXCEPTION ''injected''; END'");
            await db.Database.ExecuteSqlRawAsync("CREATE TRIGGER d48_injected_failure BEFORE INSERT ON outbox_messages FOR EACH ROW " +
                                                 "WHEN (NEW.event_type = 'QuoteSentEvent') EXECUTE FUNCTION d48_injected_failure()");
        }

        try
        {
            (await SendAsync(support.Client, quote, 2)).StatusCode.Should().Be(HttpStatusCode.InternalServerError);
        }
        finally
        {
            await using var db = _kit.Db();
            await db.Database.ExecuteSqlRawAsync("DROP TRIGGER d48_injected_failure ON outbox_messages; DROP FUNCTION d48_injected_failure()");
        }

        var stored = await _kit.LoadAsync(quote);
        (stored.Version(1).Status, stored.Version(1).SupersededAtUtc, stored.Version(2).Status, stored.Version(2).ContentHash)
            .Should().Be((QuoteVersionStatus.Sent, (DateTime?)null, QuoteVersionStatus.Draft, (string?)null));
        ((await _kit.AuditAsync(quote)).Count, (await _kit.OutboxAsync(quote)).Count).Should().Be((auditBefore, outboxBefore));

        await SentAsync(support.Client, quote, "sent", 2);
        (await _kit.LoadAsync(quote)).Version(1).Status.Should().Be(QuoteVersionStatus.Superseded);
    }

    [PostgresFact]
    public async Task ValidityStartsWhenTheVersionIsSent_NotWhenItWasDrafted()
    {
        var (support, finance) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Validity Ltd");
        var item = await _kit.ItemAsync();
        try
        {
            var direct = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)], validityDays: 10);
            var pending = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net * 90 / 100)], validityDays: 10);   // over the authority

            factory.Clock.Offset = TimeSpan.FromDays(1);
            await SentAsync(support.Client, direct, "sent");
            await SentAsync(support.Client, pending, "pending_approval");
            var d = (await _kit.LoadAsync(direct)).Version(1);
            d.ValidUntilUtc.Should().Be(QuoteValidity.ValidUntilUtc(d.SentAtUtc!.Value, 10));
            d.ValidUntilUtc.Should().NotBe(QuoteValidity.ValidUntilUtc(d.CreatedAtUtc, 10), "the days count from the send, not from the draft");

            factory.Clock.Offset = TimeSpan.FromDays(2);   // the approval comes the day after the send
            (await _kit.ApproveAsync(finance, pending)).StatusCode.Should().Be(HttpStatusCode.OK);
            var p = (await _kit.LoadAsync(pending)).Version(1);
            (p.Status, p.SentAtUtc, p.ApprovedAtUtc).Should().Be((QuoteVersionStatus.Sent, p.ApprovedAtUtc, p.SentAtUtc));
            p.SentAtUtc.Should().BeAfter(p.SubmittedAtUtc!.Value.AddHours(23));
            p.ValidUntilUtc.Should().Be(QuoteValidity.ValidUntilUtc(p.SentAtUtc!.Value, 10), "an approval is the send: the validity starts at it");
            p.ValidUntilUtc.Should().NotBe(QuoteValidity.ValidUntilUtc(p.SubmittedAtUtc.Value, 10));
        }
        finally
        {
            factory.Clock.Offset = TimeSpan.Zero;
        }
    }

    [PostgresFact]
    public async Task ASend_TakesTheDeliverySiteAsItIsNow_AndARemovedSiteIsRefused()
    {
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Site Ltd");
        var item = await _kit.ItemAsync();
        var seller = await SellerOfAsync(item.Variant);
        object Fixed(Guid site) => new { shippingMode = "fixed", shipToSiteId = site, shippingFees = new[] { new { sellerId = seller, shippingMinor = 3_000L } } };

        var moved = await SiteAsync(w.Company, "Tejgaon plant");
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)], header: Fixed(moved));
        await SqlAsync($"UPDATE company_sites SET line1 = 'House 9, Road 4', contact_name = 'Selina Akter' WHERE id = {moved}");
        var v = (await SentAsync(support.Client, quote, "sent")).GetProperty("quote").GetProperty("versions")[0].GetProperty("shipTo");
        (v.GetProperty("line1").GetString(), v.GetProperty("recipientName").GetString(), v.GetProperty("siteName").GetString())
            .Should().Be(("House 9, Road 4", "Selina Akter", "Tejgaon plant"), "the buyer is offered delivery to the site as it is at the send");
        var stored = await _kit.LoadAsync(quote);
        QuoteContentHash.Matches(stored, stored.Version(1), stored.Version(1).ContentHash).Should().BeTrue("the hash covers the refreshed address");

        var gone = await SiteAsync(w.Company, "Closed depot");
        var orphan = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)], header: Fixed(gone));
        await SqlAsync($"DELETE FROM company_sites WHERE id = {gone}");
        (await CodeAsync(await SendAsync(support.Client, orphan), HttpStatusCode.Conflict)).Should().Be("QUOTE_DELIVERY_SITE_GONE");
        var draft = (await _kit.LoadAsync(orphan)).Version(1);
        (draft.Status, draft.ShipTo!.SiteName, draft.ContentHash).Should().Be((QuoteVersionStatus.Draft, "Closed depot", (string?)null));
        (await _kit.OutboxAsync(orphan)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task ALineBelowCost_IsRefused_UnlessConfigAllows_AndALineWithoutCost_IsTreatedAsBelowFloor()
    {
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Cost Ltd");
        var costed = await _kit.ItemAsync(costPercent: 50);
        var belowCost = await _kit.DraftAsync(support.Client, w, [Line(costed, costed.Net * 40 / 100, reason: "Clearance of old stock")]);

        (await CodeAsync(await SendAsync(support.Client, belowCost), HttpStatusCode.Conflict)).Should().Be("QUOTE_BELOW_COST");
        (await _kit.LoadAsync(belowCost)).Version(1).Status.Should().Be(QuoteVersionStatus.Draft);
        ((await _kit.AuditAsync(belowCost, "quote.submitted_for_approval")).Count, (await _kit.OutboxAsync(belowCost)).Count).Should().Be((0, 0));

        // Where the host allows prices below cost (Development/Testing only), the line is still below the floor: it waits for approval.
        using (var allowing = factory.WithWebHostBuilder(b => b.UseSetting("Pricing:AllowBelowCost", "true")))
        {
            var client = allowing.CreateClient();
            client.DefaultRequestHeaders.Authorization = support.Client.DefaultRequestHeaders.Authorization;
            var sent = await SentAsync(client, belowCost, "pending_approval");
            Ids(sent.GetProperty("flags").GetProperty("belowFloorLineIds")).Should().HaveCount(1);
        }

        // No cost recorded: the floor cannot be computed, so the line counts as below it (fail closed), even at the full price.
        var uncosted = await _kit.ItemAsync(costPercent: null);
        var noCost = await _kit.DraftAsync(support.Client, w, [Line(uncosted, uncosted.Net)]);
        (await CodeAsync(await SendAsync(support.Client, noCost), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR", "it needs a below-floor reason");
        await EditAsync(support.Client, noCost, 1, [Line(uncosted, uncosted.Net, reason: "Cost not yet entered by the seller")]);
        var flags = (await SentAsync(support.Client, noCost, "pending_approval")).GetProperty("flags");
        (Ids(flags.GetProperty("belowFloorLineIds")).Count, Ids(flags.GetProperty("overAuthorityLineIds")).Count).Should().Be((1, 0));
    }

    [PostgresFact]
    public async Task AnIssuedVersionCannotBeEdited_SentAgainRecalledOrItsApprovalRejected()
    {
        var (support, finance) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Frozen Ltd");
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        var sent = (await SentAsync(support.Client, quote, "sent")).GetProperty("quote").GetProperty("versions")[0];
        var rowVersion = sent.GetProperty("rowVersion").GetUInt32();

        (await CodeAsync(await support.Client.PutAsync($"/api/v1/admin/quotes/{quote}/versions/1",
                JsonContent(new { rowVersion, validityDays = 15, lines = new[] { Line(item, 1) } })), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_NOT_EDITABLE");
        (await CodeAsync(await SendAsync(support.Client, quote), HttpStatusCode.Conflict)).Should().Be("QUOTE_INVALID_TRANSITION");
        (await CodeAsync(await PostAsync(support.Client, Url(quote, 1, "recall")), HttpStatusCode.Conflict)).Should().Be("QUOTE_INVALID_TRANSITION");
        (await CodeAsync(await PostAsync(finance.Client, Url(quote, 1, "reject-approval"), new { comment = "Not pending at all" }), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_INVALID_TRANSITION");
        (await CodeAsync(await SendAsync(support.Client, quote, 7), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR", "no such version");

        var stored = await _kit.LoadAsync(quote);
        stored.Version(1).Lines.Single().UnitPriceMinor.Should().Be(item.Net);
        QuoteContentHash.Matches(stored, stored.Version(1), sent.GetProperty("contentHash").GetString()).Should().BeTrue("the sent content is what it was");
        (await _kit.AuditAsync(quote, "quote.sent")).Should().ContainSingle();
    }

    [PostgresFact]
    public async Task RecallAndRejectApproval_ReturnAPendingVersionToDraft_AndWithdraw_EndsASentOne()
    {
        var (support, finance) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Back Ltd");
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net * 90 / 100)]);

        var hash = (await SentAsync(support.Client, quote, "pending_approval")).GetProperty("quote").GetProperty("versions")[0].GetProperty("contentHash").GetString();
        var recalled = (await DataAsync(await PostAsync(support.Client, Url(quote, 1, "recall")))).GetProperty("versions")[0];
        (recalled.GetProperty("status").GetString(), recalled.GetProperty("contentHash").ValueKind, recalled.GetProperty("flags").ValueKind)
            .Should().Be(("draft", JsonValueKind.Null, JsonValueKind.Null));
        (await _kit.AuditAsync(quote, "quote.recalled")).Single().OldValuesJson.Should().Contain(hash!);
        await EditAsync(support.Client, quote, 1, [Line(item, item.Net * 91 / 100)]);   // a Draft again: editable
        (await CodeAsync(await PostAsync(support.Client, Url(quote, 1, "recall")), HttpStatusCode.Conflict)).Should().Be("QUOTE_INVALID_TRANSITION");

        await SentAsync(support.Client, quote, "pending_approval");
        (await CodeAsync(await PostAsync(finance.Client, Url(quote, 1, "reject-approval"), new { comment = "no" }), HttpStatusCode.BadRequest))
            .Should().Be("VALIDATION_ERROR", "a rejection tells the author why (5 to 500 characters)");
        var rejected = (await DataAsync(await PostAsync(finance.Client, Url(quote, 1, "reject-approval"), new { comment = "Too low for this account" })))
            .GetProperty("versions")[0];
        (rejected.GetProperty("status").GetString(), rejected.GetProperty("approvalRejectedBy").GetGuid(), rejected.GetProperty("approvalComment").GetString())
            .Should().Be(("draft", finance.Id, "Too low for this account"));
        (await _kit.AuditAsync(quote, "quote.approval_rejected")).Single().ActorId.Should().Be(finance.Id);

        await EditAsync(support.Client, quote, 1, [Line(item, item.Net)]);
        await SentAsync(support.Client, quote, "sent");
        (await CodeAsync(await PostAsync(support.Client, Url(quote, 1, "withdraw"), new { reason = "x" }), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        var withdrawn = (await DataAsync(await PostAsync(support.Client, Url(quote, 1, "withdraw"), new { reason = "Supplier price went up" }))).GetProperty("versions")[0];
        (withdrawn.GetProperty("status").GetString(), withdrawn.GetProperty("withdrawnBy").GetGuid(), withdrawn.GetProperty("withdrawReason").GetString())
            .Should().Be(("withdrawn", support.Id, "Supplier price went up"));
        (await CodeAsync(await PostAsync(support.Client, Url(quote, 1, "withdraw"), new { reason = "Supplier price went up" }), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_INVALID_TRANSITION");
        (await _kit.OutboxAsync(quote)).Should().Equal(nameof(QuoteApprovalRequestedEvent), nameof(QuoteApprovalRequestedEvent), nameof(QuoteSentEvent),
            nameof(QuoteWithdrawnEvent));
        (await _kit.AuditAsync(quote, "quote.withdrawn")).Single().NewValuesJson.Should().Contain(withdrawn.GetProperty("contentHash").GetString()!);
    }

    [PostgresFact]
    public async Task ASend_ToACompanyThatIsNoLongerActive_IsRefused()
    {
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Send Suspended Ltd");
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        await SqlAsync($"UPDATE companies SET status = 'Suspended' WHERE id = {w.Company}");

        (await CodeAsync(await SendAsync(support.Client, quote), HttpStatusCode.Conflict)).Should().Be("COMPANY_NOT_ACTIVE");
        (await _kit.LoadAsync(quote)).Version(1).Status.Should().Be(QuoteVersionStatus.Draft);
    }

    private static StringContent JsonContent(object body) => new(JsonSerializer.Serialize(body), System.Text.Encoding.UTF8, new MediaTypeHeaderValue("application/json"));
}
