using System.Net;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using Mechware.IntegrationTests.Pricing;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;
using static Mechware.IntegrationTests.Quotes.StaffQuoteKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.8 over HTTP, the approval side (ADR-0011 §5, §9, §12, §14; threat model QT6, QT7): permission and a fresh authenticator code for every
/// approval, four-eyes against author, submitter and contributors, the extra permission below the floor, the hash and the exact
/// acknowledgement, the flags checked again with the facts of the approval's moment, the audit trail, and the approval queue. Real host,
/// real staff sign-ins (one authenticator secret, a fresh code per approval); buyers never sign in. Its own class, so its own login budget.
/// </summary>
public sealed class QuoteApprovalEndpointsTests(QuoteIssueFactory factory) : IClassFixture<QuoteIssueFactory>
{
    private readonly QuoteIssueKit _kit = new(factory);

    private sealed record Pending(Guid Quote, string Hash, List<Guid> BelowFloor, List<Guid> OverAuthority, List<Guid> Lines);

    /// <summary>A quote of <paramref name="w"/> by <paramref name="author"/> sent with flags: it waits for approval.</summary>
    private async Task<Pending> PendingAsync(HttpClient author, World w, params object[] lines)
    {
        var quote = await _kit.DraftAsync(author, w, lines);
        var sent = await SentAsync(author, quote, "pending_approval");
        var v = sent.GetProperty("quote").GetProperty("versions")[0];
        return new Pending(quote, v.GetProperty("contentHash").GetString()!, Ids(sent.GetProperty("flags").GetProperty("belowFloorLineIds")),
            Ids(sent.GetProperty("flags").GetProperty("overAuthorityLineIds")), [.. v.GetProperty("lines").EnumerateArray().Select(l => l.GetProperty("id").GetGuid())]);
    }

    private static object Ack(string? hash, IEnumerable<Guid> belowFloor, IEnumerable<Guid> overAuthority, string? comment = null) =>
        new { contentHash = hash, belowFloorLineIds = belowFloor.ToArray(), overAuthorityLineIds = overAuthority.ToArray(), comment };

    private static object Ack(Pending p) => Ack(p.Hash, p.BelowFloor, p.OverAuthority);

    private async Task<HttpResponseMessage> ApproveAsync(RfqApiKit.Person who, Pending p, object? body = null) =>
        await ApproveWithAsync(who.Client, p.Quote, 1, body ?? Ack(p), await _kit.CodeAsync(who.Id));

    private async Task<QuoteVersionStatus> StatusAsync(Guid quote, int n = 1) => (await _kit.LoadAsync(quote)).Version(n).Status;

    [PostgresFact]
    public async Task Approve_WithoutStepUpOrPermission_IsRefused()
    {
        var (support, support2, finance) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Support, "support2"),
            await _kit.StaffAsync(Permissions.Roles.Finance));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve StepUp Ltd");
        var item = await _kit.ItemAsync();
        var p = await PendingAsync(support.Client, w, Line(item, item.Net * 90 / 100));
        var other = await PendingAsync(support.Client, w, Line(item, item.Net * 89 / 100));

        (await CodeAsync(await ApproveWithAsync(finance.Client, p.Quote, 1, Ack(p), null), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_REQUIRED");
        (await CodeAsync(await ApproveWithAsync(finance.Client, p.Quote, 1, Ack(p), "000000"), HttpStatusCode.Forbidden)).Should().Be("STEP_UP_FAILED");
        var code = await _kit.CodeAsync(finance.Id);
        (await ApproveWithAsync(finance.Client, other.Quote, 1, Ack(other), code)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await CodeAsync(await ApproveWithAsync(finance.Client, p.Quote, 1, Ack(p), code), HttpStatusCode.Forbidden))
            .Should().Be("STEP_UP_FAILED", "one code confirms one approval: a replay is refused");

        // Support may send, never approve; the permission is checked before any code is looked at.
        (await ApproveAsync(support2, p)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        await using (var db = _kit.Db())
            (await db.AuditLogs.CountAsync(a => a.ActorId == support2.Id && a.Action.StartsWith("user.step_up"))).Should().Be(0, "no code was checked");
        (await StatusAsync(p.Quote)).Should().Be(QuoteVersionStatus.PendingApproval);
        (await _kit.AuditAsync(p.Quote, "quote.approved")).Should().BeEmpty();

        // The approver is the caller: ids in the body are ignored.
        var body = new { contentHash = p.Hash, belowFloorLineIds = p.BelowFloor, overAuthorityLineIds = p.OverAuthority, approvedBy = support.Id, sentBy = support.Id };
        var approved = (await DataAsync(await ApproveAsync(finance, p, body))).GetProperty("versions")[0];
        (approved.GetProperty("status").GetString(), approved.GetProperty("approvedBy").GetGuid(), approved.GetProperty("sentBy").GetGuid())
            .Should().Be(("sent", finance.Id, finance.Id));
        await using var check = _kit.Db();
        (await check.AuditLogs.Where(a => a.Action == "user.step_up_passed" && a.ActorId == finance.Id).Select(a => a.NewValuesJson).ToListAsync())
            .Should().Contain(json => json!.Contains("quote.approve") && json.Contains($"quoteId={p.Quote}"), "the step-up audit names the quote it confirmed");
    }

    [PostgresFact]
    public async Task Approve_ByTheAuthorSubmitterOrAContributor_IsRefused()
    {
        var (creator, contributor, submitter, approver) = (await _kit.StaffAsync(Permissions.Roles.Admin), await _kit.StaffAsync(Permissions.Roles.Admin, "admin2"),
            await _kit.StaffAsync(Permissions.Roles.Admin, "admin3"), await _kit.StaffAsync(Permissions.Roles.Admin, "admin4"));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve FourEyes Ltd");
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(creator.Client, w, [Line(item, item.Net * 90 / 100)]);
        await EditAsync(contributor.Client, quote, 1, [Line(item, item.Net * 90 / 100, quantity: 3)]);
        var sent = await SentAsync(submitter.Client, quote, "pending_approval");
        var p = new Pending(quote, sent.GetProperty("quote").GetProperty("versions")[0].GetProperty("contentHash").GetString()!, [],
            Ids(sent.GetProperty("flags").GetProperty("overAuthorityLineIds")), []);
        var v = (await _kit.LoadAsync(quote)).Version(1);
        (v.CreatedBy, v.SubmittedBy).Should().Be((creator.Id, submitter.Id));
        v.Contributors.Select(c => c.UserId).Should().BeEquivalentTo([creator.Id, contributor.Id], "the submitter only sent it");

        foreach (var (who, role) in new[] { (creator, "creator"), (contributor, "contributor"), (submitter, "submitter") })
        {
            var item0 = (await DataAsync(await who.Client.GetAsync("/api/v1/admin/quotes/approvals?pageSize=50"))).GetProperty("items").EnumerateArray()
                .Single(i => i.GetProperty("quoteId").GetGuid() == quote);
            item0.GetProperty("canApprove").GetBoolean().Should().BeFalse($"the queue tells the {role} too");
            (await CodeAsync(await ApproveAsync(who, p), HttpStatusCode.Conflict)).Should().Be("QUOTE_FOUR_EYES", role);
        }
        (await StatusAsync(quote)).Should().Be(QuoteVersionStatus.PendingApproval);

        (await ApproveAsync(approver, p)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _kit.LoadAsync(quote)).Version(1).ApprovedBy.Should().Be(approver.Id);
    }

    [PostgresFact]
    public async Task Approve_BelowFloorWithoutTheExtraPermission_IsRefused()
    {
        var (support, finance, admin) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance),
            await _kit.StaffAsync(Permissions.Roles.Admin));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve Floor Ltd");
        var item = await _kit.ItemAsync(costPercent: 50);
        var p = await PendingAsync(support.Client, w, Line(item, item.Net * 52 / 100, reason: "Strategic account, owner agreed"));
        p.BelowFloor.Should().HaveCount(1);

        (await CodeAsync(await ApproveAsync(finance, p), HttpStatusCode.Forbidden)).Should().Be("BELOW_FLOOR_PERMISSION_REQUIRED", "Finance approves discounts, not prices below the floor");
        (await StatusAsync(p.Quote)).Should().Be(QuoteVersionStatus.PendingApproval);
        (await ApproveAsync(admin, p)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await StatusAsync(p.Quote)).Should().Be(QuoteVersionStatus.Sent);
    }

    [PostgresFact]
    public async Task Approve_WithoutAcknowledgingExactlyTheFlaggedLines_IsRefused()
    {
        var (support, admin) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Admin));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve Ack Ltd");
        var (a, b, c) = (await _kit.ItemAsync(), await _kit.ItemAsync(), await _kit.ItemAsync());
        var p = await PendingAsync(support.Client, w, Line(a, a.Net * 52 / 100, reason: "Strategic account, owner agreed"), Line(b, b.Net * 90 / 100), Line(c, c.Net));
        var (l1, l2, l3) = (p.Lines[0], p.Lines[1], p.Lines[2]);
        (p.BelowFloor, p.OverAuthority).Should().BeEquivalentTo((new[] { l1 }, new[] { l1, l2 }));

        var wrong = new (Guid[] Below, Guid[] Over, string Why)[]
        {
            ([], [], "nothing acknowledged"),
            ([l1], [l2], "a subset of the over-authority lines"),
            ([l1], [l1, l2, l3], "a superset"),
            ([l1, l2], [l1, l2], "a line acknowledged as below the floor that is not"),
            ([], [l1, l2], "the below-floor line not acknowledged"),
        };
        foreach (var (below, over, why) in wrong)
        {
            var response = await ApproveAsync(admin, p, Ack(p.Hash, below, over));
            var (code, message) = await RfqApiKit.ErrorAsync(response, HttpStatusCode.Conflict);
            code.Should().Be("QUOTE_FLAGS_NOT_ACKNOWLEDGED", why);
            message.Should().Contain(l1.ToString()).And.Contain(l2.ToString(), "the refusal names the lines flagged now");
        }

        (await StatusAsync(p.Quote)).Should().Be(QuoteVersionStatus.PendingApproval);
        (await ApproveAsync(admin, p, Ack(p.Hash, [l1], [l2, l1]))).StatusCode.Should().Be(HttpStatusCode.OK, "the order of the ids does not matter");
    }

    [PostgresFact]
    public async Task Approve_WithAStaleContentHash_IsRefused()
    {
        var (support, finance) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve Hash Ltd");
        var item = await _kit.ItemAsync();
        var p = await PendingAsync(support.Client, w, Line(item, item.Net * 90 / 100));
        var otherHash = (await PendingAsync(support.Client, w, Line(item, item.Net * 90 / 100))).Hash;

        foreach (var hash in new[] { otherHash, new string('0', 64), p.Hash.ToUpperInvariant(), p.Hash[..63] })
            (await CodeAsync(await ApproveAsync(finance, p, Ack(hash, p.BelowFloor, p.OverAuthority)), HttpStatusCode.Conflict)).Should().Be("QUOTE_CHANGED", hash);
        (await CodeAsync(await ApproveAsync(finance, p, Ack(null, p.BelowFloor, p.OverAuthority)), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");

        // Recalled and sent again with other content: the hash the approver saw before no longer approves anything.
        await DataAsync(await PostAsync(support.Client, Url(p.Quote, 1, "recall")));
        await EditAsync(support.Client, p.Quote, 1, [Line(item, item.Net * 80 / 100)]);
        await SentAsync(support.Client, p.Quote, "pending_approval");
        (await CodeAsync(await ApproveAsync(finance, p), HttpStatusCode.Conflict)).Should().Be("QUOTE_CHANGED");
        (await StatusAsync(p.Quote)).Should().Be(QuoteVersionStatus.PendingApproval);
        (await _kit.ApproveAsync(finance, p.Quote)).StatusCode.Should().Be(HttpStatusCode.OK, "the hash of what is pending now");
    }

    [PostgresFact]
    public async Task FlagsAreRecheckedAtApproval_WithTheCostFloorAndPriceOfThatMoment()
    {
        var (support, finance, admin) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance),
            await _kit.StaffAsync(Permissions.Roles.Admin));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve Recheck Ltd");

        // The cost rose after the send: the line is below the floor now.
        var rising = await _kit.ItemAsync(costPercent: 50);
        var p = await PendingAsync(support.Client, w, Line(rising, rising.Net * 90 / 100, reason: "Volume deal agreed with the buyer"));
        (p.BelowFloor.Count, p.OverAuthority.Count).Should().Be((0, 1));
        var newCost = rising.Net * 85 / 100;   // floor = 93.5 % of the net price
        await _kit.SetCostAsync(rising.Variant, newCost);
        var (code, message) = await RfqApiKit.ErrorAsync(await ApproveAsync(admin, p), HttpStatusCode.Conflict);
        (code, message.Contains(p.Lines[0].ToString())).Should().Be(("QUOTE_FLAGS_NOT_ACKNOWLEDGED", true), "the flags of the send are stale");
        var now = Ack(p.Hash, p.Lines, p.Lines);
        (await CodeAsync(await ApproveAsync(finance, p, now), HttpStatusCode.Forbidden)).Should().Be("BELOW_FLOOR_PERMISSION_REQUIRED");
        var approved = (await DataAsync(await ApproveAsync(admin, p, now))).GetProperty("versions")[0];
        Ids(approved.GetProperty("flags").GetProperty("belowFloorLineIds")).Should().Equal(p.Lines[0]);
        approved.GetProperty("lines")[0].GetProperty("cost").GetProperty("costMinorAtCheck").GetInt64().Should().Be(newCost, "the approval records its own check");

        // Without a reason on the line, a line that fell below the floor cannot be approved: the approver rejects it.
        var noReason = await _kit.ItemAsync(costPercent: 50);
        var q = await PendingAsync(support.Client, w, Line(noReason, noReason.Net * 90 / 100));
        await _kit.SetCostAsync(noReason.Variant, noReason.Net * 85 / 100);
        (await CodeAsync(await ApproveAsync(admin, q, Ack(q.Hash, q.Lines, q.Lines)), HttpStatusCode.Conflict)).Should().Be("BELOW_FLOOR_REASON_REQUIRED");

        // The buyer's own price fell (a contract price list) after the send: the same quote price is now within the authority.
        var w2 = await _kit.CompanyAsync("Approve Contract Ltd");
        var item = await _kit.ItemAsync(costPercent: 30);
        var r = await PendingAsync(support.Client, w2, Line(item, item.Net * 90 / 100));
        var contract = item.Net * 92 / 100;
        await using (var db = _kit.Db())
        {
            var at = PriceListTestData.WholeSecond(DateTime.UtcNow.AddMinutes(-5));
            await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, w2.Company, at, at.AddDays(30), at, new PriceListTestData.Entry(item.Variant, 1, contract));
        }

        (await CodeAsync(await ApproveAsync(finance, r), HttpStatusCode.Conflict)).Should().Be("QUOTE_FLAGS_NOT_ACKNOWLEDGED", "the flag is gone now");
        var cleared = (await DataAsync(await ApproveAsync(finance, r, Ack(r.Hash, [], [])))).GetProperty("versions")[0];
        (Ids(cleared.GetProperty("flags").GetProperty("overAuthorityLineIds")).Count, cleared.GetProperty("lines")[0].GetProperty("referenceUnitPriceMinor").GetInt64(),
            cleared.GetProperty("lines")[0].GetProperty("referenceSource").GetString()).Should().Be((0, contract, "contract"));
    }

    [PostgresFact]
    public async Task ADiscountBeyondTheStaffLimitNeedsASecondApprover_MeasuredAgainstTheBuyersResolverPrice_NotThePreviousVersion()
    {
        var (support, finance) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve Ratchet Ltd");
        var item = await _kit.ItemAsync(costPercent: 30);
        var contract = item.Net * 80 / 100;
        await using (var db = _kit.Db())
        {
            var at = PriceListTestData.WholeSecond(DateTime.UtcNow.AddMinutes(-5));
            await PriceListTestData.ApprovedAsync(db, PriceListScope.Company, w.Company, at, at.AddDays(30), at, new PriceListTestData.Entry(item.Variant, 1, contract));
        }

        // At the company's own contract price: within the authority, although it is 20 % below the public price.
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, contract)]);
        await SentAsync(support.Client, quote, "sent");

        // 10 % below the contract price: a second person approves; Support cannot.
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        var v2Price = contract * 90 / 100;
        await EditAsync(support.Client, quote, 2, [Line(item, v2Price)]);
        await SentAsync(support.Client, quote, "pending_approval", 2);
        (await ApproveWithAsync(support.Client, quote, 2, new { contentHash = "x" }, await _kit.CodeAsync(support.Id))).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        (await _kit.ApproveAsync(finance, quote, 2)).StatusCode.Should().Be(HttpStatusCode.OK);
        var stored = await _kit.LoadAsync(quote);
        (stored.Version(1).Status, stored.Version(2).Status).Should().Be((QuoteVersionStatus.Superseded, QuoteVersionStatus.Sent));

        // 4 % below version 2 is still 13.6 % below the buyer's own price: the discount is not measured against the previous version (no ratchet).
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        await EditAsync(support.Client, quote, 3, [Line(item, v2Price * 96 / 100)]);
        var third = await SentAsync(support.Client, quote, "pending_approval", 3);
        Ids(third.GetProperty("flags").GetProperty("overAuthorityLineIds")).Should().HaveCount(1);
        third.GetProperty("quote").GetProperty("versions")[2].GetProperty("lines")[0].GetProperty("referenceUnitPriceMinor").GetInt64().Should().Be(contract);
    }

    [PostgresFact]
    public async Task AStaffMemberWhoBelongsToTheCompany_CannotEditApproveOrSendItsQuote()
    {
        var (support, insiderSupport, insiderFinance) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Support, "insider"),
            await _kit.StaffAsync(Permissions.Roles.Finance, "insider"));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve Insider Ltd", (insiderSupport.Id, CompanyRoles.Requester), (insiderFinance.Id, CompanyRoles.Requester));
        var item = await _kit.ItemAsync();
        var draft = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);

        (await CodeAsync(await SendAsync(insiderSupport.Client, draft), HttpStatusCode.Forbidden)).Should().Be("QUOTE_CONFLICT_OF_INTEREST", "send half of QT6");
        (await StatusAsync(draft)).Should().Be(QuoteVersionStatus.Draft);

        var p = await PendingAsync(support.Client, w, Line(item, item.Net * 90 / 100));
        (await CodeAsync(await ApproveAsync(insiderFinance, p), HttpStatusCode.Forbidden)).Should().Be("QUOTE_CONFLICT_OF_INTEREST", "approve half of QT6");
        (await StatusAsync(p.Quote)).Should().Be(QuoteVersionStatus.PendingApproval);
        var queue = await DataAsync(await insiderFinance.Client.GetAsync("/api/v1/admin/quotes/approvals?pageSize=50"));
        queue.GetProperty("items").EnumerateArray().Single(i => i.GetProperty("quoteId").GetGuid() == p.Quote).GetProperty("canApprove").GetBoolean()
            .Should().BeFalse("the queue says so too");
    }

    [PostgresFact]
    public async Task EveryQuoteTransition_IsAudited_WithActorIpAndContentHash()
    {
        var (support, finance) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve Audit Ltd");
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net * 90 / 100)]);
        async Task<string> HashAsync(int n = 1) => (await _kit.LoadAsync(quote)).Version(n).ContentHash!;

        var expected = new List<(string Action, Guid Actor, string Hash)>();
        await SentAsync(support.Client, quote, "pending_approval");
        expected.Add(("quote.submitted_for_approval", support.Id, await HashAsync()));
        expected.Add(("quote.approval_rejected", finance.Id, await HashAsync()));
        await DataAsync(await PostAsync(finance.Client, Url(quote, 1, "reject-approval"), new { comment = "Ask the buyer for a larger quantity" }));
        await EditAsync(support.Client, quote, 1, [Line(item, item.Net * 90 / 100, quantity: 5)]);
        await SentAsync(support.Client, quote, "pending_approval");
        expected.Add(("quote.submitted_for_approval", support.Id, await HashAsync()));
        expected.Add(("quote.recalled", support.Id, await HashAsync()));
        await DataAsync(await PostAsync(support.Client, Url(quote, 1, "recall")));
        await SentAsync(support.Client, quote, "pending_approval");
        expected.Add(("quote.submitted_for_approval", support.Id, await HashAsync()));
        (await _kit.ApproveAsync(finance, quote, comment: "Agreed by phone")).StatusCode.Should().Be(HttpStatusCode.OK);
        expected.Add(("quote.approved", finance.Id, await HashAsync()));
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{quote}/revise"));
        await EditAsync(support.Client, quote, 2, [Line(item, item.Net, quantity: 5)]);
        await SentAsync(support.Client, quote, "sent", 2);
        expected.Add(("quote.sent", support.Id, await HashAsync(2)));
        await DataAsync(await PostAsync(support.Client, Url(quote, 2, "withdraw"), new { reason = "The buyer asked us to hold" }));
        expected.Add(("quote.withdrawn", support.Id, await HashAsync(2)));

        var transitions = new HashSet<string>(expected.Select(e => e.Action));
        var rows = (await _kit.AuditAsync(quote)).Where(a => transitions.Contains(a.Action)).ToList();
        rows.Select(a => (a.Action, a.ActorId)).Should().Equal(expected.Select(e => (e.Action, (Guid?)e.Actor)), "one row per transition, in order, by its actor");
        rows.Should().OnlyContain(a => a.ActorIp == QuoteIssueFactory.ClientIp);
        rows.Zip(expected).Should().OnlyContain(x => (x.First.NewValuesJson + x.First.OldValuesJson).Contains(x.Second.Hash), "each row names the content it moved");
        using (var approved = JsonDocument.Parse(rows.Single(a => a.Action == "quote.approved").NewValuesJson!))
            (approved.RootElement.GetProperty("ApprovalComment").GetString(), approved.RootElement.GetProperty("SupersededVersionNo").ValueKind)
                .Should().Be(("Agreed by phone", JsonValueKind.Null));
        (await _kit.OutboxAsync(quote)).Should().Equal(nameof(QuoteApprovalRequestedEvent), nameof(QuoteApprovalRequestedEvent), nameof(QuoteApprovalRequestedEvent),
            nameof(QuoteSentEvent), nameof(QuoteSentEvent), nameof(QuoteWithdrawnEvent));
    }

    [PostgresFact]
    public async Task TheApprovalQueue_ListsWaitingVersionsOldestFirst_AndSaysWhoMayApprove()
    {
        var (support, finance, admin, admin2) = (await _kit.StaffAsync(Permissions.Roles.Support), await _kit.StaffAsync(Permissions.Roles.Finance),
            await _kit.StaffAsync(Permissions.Roles.Admin), await _kit.StaffAsync(Permissions.Roles.Admin, "admin2"));
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Approve Queue Ltd");
        var item = await _kit.ItemAsync(costPercent: 50);
        var discount = await PendingAsync(support.Client, w, Line(item, item.Net * 90 / 100));
        var belowFloor = await PendingAsync(admin.Client, w, Line(item, item.Net * 52 / 100, reason: "Strategic account, owner agreed"));
        var draft = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        var sent = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        await SentAsync(support.Client, sent, "sent");
        var closed = await PendingAsync(support.Client, w, Line(item, item.Net * 90 / 100));
        await DataAsync(await PostAsync(support.Client, $"/api/v1/admin/quotes/{closed.Quote}/close", new { reason = "Duplicate of another quote" }));

        async Task<List<JsonElement>> QueueAsync(RfqApiKit.Person who)
        {
            var response = await who.Client.GetAsync("/api/v1/admin/quotes/approvals?pageSize=50");
            (response.Headers.CacheControl!.Private, response.Headers.CacheControl.NoStore).Should().Be((true, true));
            return [.. (await DataAsync(response)).GetProperty("items").EnumerateArray()];
        }

        var forFinance = await QueueAsync(finance);
        forFinance.Select(i => i.GetProperty("quoteId").GetGuid()).Where(id => id == discount.Quote || id == belowFloor.Quote || id == draft
            || id == sent || id == closed.Quote).Should().Equal([discount.Quote, belowFloor.Quote], "only waiting versions of open quotes, oldest first");
        var first = forFinance.Single(i => i.GetProperty("quoteId").GetGuid() == discount.Quote);
        (first.GetProperty("versionNo").GetInt32(), first.GetProperty("submittedBy").GetGuid(), first.GetProperty("submitterName").GetString(),
            first.GetProperty("lineCount").GetInt32(), first.GetProperty("canApprove").GetBoolean()).Should().Be((1, support.Id, "Rfq support", 1, true));
        Ids(first.GetProperty("flags").GetProperty("overAuthorityLineIds")).Should().Equal(discount.OverAuthority);
        forFinance.Single(i => i.GetProperty("quoteId").GetGuid() == belowFloor.Quote).GetProperty("canApprove").GetBoolean()
            .Should().BeFalse("below the floor needs quotes.approve_below_floor");

        var forAuthor = await QueueAsync(admin);
        forAuthor.Single(i => i.GetProperty("quoteId").GetGuid() == belowFloor.Quote).GetProperty("canApprove").GetBoolean().Should().BeFalse("four-eyes");
        forAuthor.Single(i => i.GetProperty("quoteId").GetGuid() == discount.Quote).GetProperty("canApprove").GetBoolean().Should().BeTrue();
        (await QueueAsync(admin2)).Single(i => i.GetProperty("quoteId").GetGuid() == belowFloor.Quote).GetProperty("canApprove").GetBoolean().Should().BeTrue();

        var page = await DataAsync(await finance.Client.GetAsync("/api/v1/admin/quotes/approvals?page=1&pageSize=1"));
        (page.GetProperty("items").GetArrayLength(), page.GetProperty("total").GetInt32() >= 2).Should().Be((1, true));
    }
}
