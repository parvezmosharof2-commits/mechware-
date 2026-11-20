using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Companies;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteIssueKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.9 over HTTP: <c>POST companies/{companyId}/quotes/{quoteId}/accept</c> (ADR-0011 §8.1, §8.2, §12, §13; threat model QT1, QT2, QT4,
/// QT11, QT15). Real host, real sign-ins (staff write and send the quotes; buyers accept); each person signs in once for the class, which has
/// its own host and so its own login budget. One 404 body for everything the caller may not see, 403 for a member without the Buyer role,
/// the body cannot carry lines, prices, coupons or a company, 201 / 200 replay / 409, never a 500 in a race.
/// </summary>
public sealed class QuoteAcceptEndpointsTests(QuoteIssueFactory factory) : IClassFixture<QuoteIssueFactory>
{
    private readonly QuoteIssueKit _kit = new(factory);

    private Task<RfqApiKit.Person> PersonAsync(string name) => _kit.StaffAsync(Permissions.Roles.Buyer, name);

    private static string Url(Guid company, Guid quote) => $"/api/v1/companies/{company}/quotes/{quote}/accept";

    private static Task<HttpResponseMessage> AcceptAsync(HttpClient who, Guid company, Guid quote, object body, string? key = "new")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, Url(company, quote)) { Content = JsonContent.Create(body) };
        if (key is not null) request.Headers.TryAddWithoutValidation("Idempotency-Key", key == "new" ? "acc-" + Guid.NewGuid().ToString("N") : key);
        return who.SendAsync(request);
    }

    /// <summary>What the accept screen sends: the version, its hash and its total (calculated delivery is free in the test host).</summary>
    private static Dictionary<string, object?> Body(JsonElement v, bool onCredit = true) => new()
    {
        ["versionNo"] = v.GetProperty("versionNo").GetInt32(),
        ["contentHash"] = v.GetProperty("contentHash").GetString(),
        ["expectedGrandTotalMinor"] = v.GetProperty("goodsTotalMinor").GetInt64(),
        ["payOnCredit"] = onCredit,
    };

    private sealed record Sent(World W, Guid Quote, JsonElement Version, Item Item);

    /// <summary>An active company on credit with <paramref name="members"/>; one item, quoted at its net price ×2 and sent by Support.</summary>
    private async Task<Sent> SentQuoteAsync(params (Guid User, CompanyRoles Roles)[] members)
    {
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        await _kit.TenPercentFloorAsync();
        var w = await _kit.CompanyAsync("Accept Http Ltd", members);
        await using (var db = _kit.Db())
            await new CompanyService(db).AdminSetCreditAsync(w.Company, new SetCreditRequest(CreditTerms.Net30, 100_000_000));
        var item = await _kit.ItemAsync();
        var quote = await _kit.DraftAsync(support.Client, w, [Line(item, item.Net)]);
        await SentAsync(support.Client, quote, "sent");
        return new Sent(w, quote, await VersionAsync(support.Client, quote), item);
    }

    private async Task<int> OrdersOfAsync(Guid quote)
    {
        await using var db = _kit.Db();
        return await db.Orders.CountAsync(o => o.QuoteId == quote);
    }

    private async Task SqlAsync(FormattableString sql)
    {
        await using var db = _kit.Db();
        await db.Database.ExecuteSqlInterpolatedAsync(sql);
    }

    // ---- QT2 -------------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AcceptingUsesTheSnapshotPrices_NotTheClients()
    {
        var buyer = await PersonAsync("buyer-a");
        var s = await SentQuoteAsync((buyer.Id, CompanyRoles.Buyer));
        var body = Body(s.Version);
        body["unitPriceMinor"] = 1;
        body["grandTotalMinor"] = 1;
        body["discountMinor"] = 999_999;

        var r = await AcceptAsync(buyer.Client, s.W.Company, s.Quote, body);
        var data = await StaffQuoteKit.DataAsync(r, HttpStatusCode.Created);
        RfqApiKit.ShouldBePrivate(r);
        var orderId = data.GetProperty("orderId").GetGuid();
        r.Headers.Location!.ToString().Should().Be($"/api/v1/orders/{orderId}");
        (data.GetProperty("status").GetString(), data.GetProperty("grandTotalMinor").GetInt64(), data.GetProperty("replayed").GetBoolean())
            .Should().Be(("confirmed", s.Version.GetProperty("goodsTotalMinor").GetInt64(), false));
        var number = data.GetProperty("quoteNumber").GetString();
        number.Should().StartWith("MWQ-");

        var order = await StaffQuoteKit.DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/{orderId}"));
        order.GetProperty("quoteNumber").GetString().Should().Be(number);
        var line = order.GetProperty("subOrders")[0].GetProperty("items")[0];
        (line.GetProperty("unitPriceMinor").GetInt64(), line.GetProperty("quantity").GetInt32(), line.GetProperty("discountMinor").GetInt64(),
            line.GetProperty("priceSource").GetString()).Should().Be((s.Item.Net, 2, 0L, "quote"));
    }

    [PostgresFact]
    public async Task AcceptBody_CannotCarryLinesQuantitiesPricesCouponsOrACompany()
    {
        var buyer = await PersonAsync("buyer-a");
        var other = await _kit.CompanyAsync("Other Buyer Ltd", (buyer.Id, CompanyRoles.Buyer));
        var s = await SentQuoteAsync((buyer.Id, CompanyRoles.Buyer));
        var body = Body(s.Version);
        body["lines"] = new[] { new { variantId = s.Item.Variant, quantity = 9, unitPriceMinor = 1 } };
        body["quantity"] = 9;
        body["couponCodes"] = new[] { "HALF-OFF" };
        body["companyId"] = other.Company;

        var data = await StaffQuoteKit.DataAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, body), HttpStatusCode.Created);
        await using var db = _kit.Db();
        var order = await db.Orders.AsNoTracking().Include(o => o.SubOrders).ThenInclude(x => x.Items).SingleAsync(o => o.Id == data.GetProperty("orderId").GetGuid());
        (order.CompanyId, order.QuoteId, order.DiscountMinor).Should().Be((s.W.Company, s.Quote, 0L), "the order is the quote's, in the quote's company");
        order.SubOrders.Single().Items.Single().Should().BeEquivalentTo(new { VariantId = s.Item.Variant, Quantity = 2, UnitPriceMinor = s.Item.Net });
    }

    // ---- QT1, QT11: who may accept ----------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Idor_AcceptingAQuoteTheCallerMayNotSee_IsOne404_AndPlacesNothing()
    {
        var (buyer, outsider, lone, requester) = (await PersonAsync("buyer-a"), await PersonAsync("outsider"), await PersonAsync("lone"), await PersonAsync("requester"));
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        var theirs = await _kit.CompanyAsync("Outsider Ltd", (outsider.Id, CompanyRoles.Admin | CompanyRoles.Buyer), (buyer.Id, CompanyRoles.Buyer));
        var s = await SentQuoteAsync((buyer.Id, CompanyRoles.Buyer), (requester.Id, CompanyRoles.Requester));
        var body = Body(s.Version);

        var answers = new List<string>
        {
            await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(outsider.Client, s.W.Company, s.Quote, body), "another company's admin and buyer"),
            await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(outsider.Client, theirs.Company, s.Quote, body), "under their own company"),
            await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(buyer.Client, theirs.Company, s.Quote, body), "a member of both companies, under the other one"),
            await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(lone.Client, s.W.Company, s.Quote, body), "a buyer of no company"),
            await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(support.Client, s.W.Company, s.Quote, body), "platform staff on a buyer route"),
            await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(requester.Client, s.W.Company, s.Quote, body), "a Requester who is not the contact"),
            await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(buyer.Client, s.W.Company, Guid.NewGuid(), body), "an unknown quote"),
        };
        var unknownVersion = Body(s.Version);
        unknownVersion["versionNo"] = 9;
        answers.Add(await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, unknownVersion), "an unknown version"));

        // A draft (version 2 after a revision) does not exist for the buyer.
        await StaffQuoteKit.DataAsync(await StaffQuoteKit.PostAsync(support.Client, $"/api/v1/admin/quotes/{s.Quote}/revise"));
        var draft = Body(s.Version);
        draft["versionNo"] = 2;
        answers.Add(await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, draft), "a draft version"));

        // A removed member, and a company that is Pending or Rejected, look like nothing is there.
        await SqlAsync($"UPDATE company_members SET is_active = false WHERE company_id = {s.W.Company} AND user_id = {buyer.Id}");
        answers.Add(await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, body), "a removed member"));
        await SqlAsync($"UPDATE company_members SET is_active = true WHERE company_id = {s.W.Company} AND user_id = {buyer.Id}");
        foreach (var status in new[] { "Pending", "Rejected" })
        {
            await SqlAsync($"UPDATE companies SET status = {status} WHERE id = {s.W.Company}");
            answers.Add(await RfqApiKit.NotFoundShapeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, body), $"a {status} company"));
        }

        answers.Distinct().Should().ContainSingle().Which.Should().Be("NOT_FOUND|Quote not found.");
        (await factory.CreateClient().PostAsJsonAsync(Url(s.W.Company, s.Quote), body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await OrdersOfAsync(s.Quote)).Should().Be(0);

        // The same buyer, back in an active company, accepts it.
        await SqlAsync($"UPDATE companies SET status = 'Active' WHERE id = {s.W.Company}");
        (await AcceptAsync(buyer.Client, s.W.Company, s.Quote, body)).StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [PostgresFact]
    public async Task Accept_ByAMemberWithoutTheBuyerRole_Is403_AndASuspendedCompanyIs409()
    {
        var (buyer, admin) = (await PersonAsync("buyer-a"), await PersonAsync("admin-only"));
        var s = await SentQuoteAsync((buyer.Id, CompanyRoles.Buyer), (admin.Id, CompanyRoles.Admin | CompanyRoles.Approver));

        var forbidden = await AcceptAsync(admin.Client, s.W.Company, s.Quote, Body(s.Version));
        (await RfqApiKit.ErrorAsync(forbidden, HttpStatusCode.Forbidden)).Code.Should().Be("COMPANY_ROLE_REQUIRED", "an Admin sees the quote but only a Buyer accepts");
        RfqApiKit.ShouldBePrivate(forbidden);

        await SqlAsync($"UPDATE companies SET status = 'Suspended' WHERE id = {s.W.Company}");
        (await StaffQuoteKit.CodeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, Body(s.Version)), HttpStatusCode.Conflict)).Should().Be("COMPANY_SUSPENDED");
        (await OrdersOfAsync(s.Quote)).Should().Be(0);
    }

    // ---- QT15 and the request itself ---------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task Accept_WithoutAnExpectedTotal_Is400()
    {
        var buyer = await PersonAsync("buyer-a");
        var s = await SentQuoteAsync((buyer.Id, CompanyRoles.Buyer));
        async Task<string> FieldOf(Dictionary<string, object?> body, string? key = "new")
        {
            var r = await AcceptAsync(buyer.Client, s.W.Company, s.Quote, body, key);
            var text = await r.Content.ReadAsStringAsync();
            r.StatusCode.Should().Be(HttpStatusCode.BadRequest, text);
            return text;
        }

        var noTotal = Body(s.Version);
        noTotal.Remove("expectedGrandTotalMinor");
        (await FieldOf(noTotal)).Should().Contain("expectedGrandTotalMinor");
        var noHash = Body(s.Version);
        noHash.Remove("contentHash");
        (await FieldOf(noHash)).Should().Contain("contentHash");
        var noVersion = Body(s.Version);
        noVersion.Remove("versionNo");
        (await FieldOf(noVersion)).Should().Contain("versionNo");
        (await FieldOf(Body(s.Version), key: null)).Should().Contain("Idempotency-Key");

        var lower = Body(s.Version);
        lower["expectedGrandTotalMinor"] = s.Version.GetProperty("goodsTotalMinor").GetInt64() - 1;
        (await StaffQuoteKit.CodeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, lower), HttpStatusCode.Conflict)).Should().Be("PRICE_CHANGED");
        (await OrdersOfAsync(s.Quote)).Should().Be(0);
    }

    // ---- QT4 over HTTP -------------------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task AcceptReplay_WithTheSameKeyButAnotherBody_Is409_AndTheSameBodyReplays200()
    {
        var buyer = await PersonAsync("buyer-a");
        var s = await SentQuoteAsync((buyer.Id, CompanyRoles.Buyer));
        var key = "acc-" + Guid.NewGuid().ToString("N");

        var first = await StaffQuoteKit.DataAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, Body(s.Version), key), HttpStatusCode.Created);
        var replay = await StaffQuoteKit.DataAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, Body(s.Version), key));
        (replay.GetProperty("orderId").GetGuid(), replay.GetProperty("replayed").GetBoolean(), replay.GetProperty("quoteNumber").GetString())
            .Should().Be((first.GetProperty("orderId").GetGuid(), true, first.GetProperty("quoteNumber").GetString()));

        (await StaffQuoteKit.CodeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, Body(s.Version, onCredit: false), key), HttpStatusCode.Conflict))
            .Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await StaffQuoteKit.CodeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, Body(s.Version)), HttpStatusCode.Conflict))
            .Should().Be("QUOTE_ALREADY_ACCEPTED", "a new key is a new order, and the version already has one");
        (await OrdersOfAsync(s.Quote)).Should().Be(1);
    }

    [PostgresFact]
    public async Task AcceptWithAStaleContentHash_Is409()
    {
        var buyer = await PersonAsync("buyer-a");
        var s = await SentQuoteAsync((buyer.Id, CompanyRoles.Buyer));
        var stale = Body(s.Version);
        stale["contentHash"] = new string('0', 64);
        (await StaffQuoteKit.CodeAsync(await AcceptAsync(buyer.Client, s.W.Company, s.Quote, stale), HttpStatusCode.Conflict)).Should().Be("QUOTE_CHANGED");
        (await OrdersOfAsync(s.Quote)).Should().Be(0);
    }

    [PostgresFact]
    public async Task TwoParallelAccepts_OverHttp_MakeOneOrder_AndNeverA500()
    {
        var (a, b) = (await PersonAsync("buyer-a"), await PersonAsync("buyer-b"));
        for (var round = 0; round < 4; round++)
        {
            var s = await SentQuoteAsync((a.Id, CompanyRoles.Buyer), (b.Id, CompanyRoles.Buyer));
            using var gate = new ManualResetEventSlim(false);
            var ta = Task.Run(() => { gate.Wait(); return AcceptAsync(a.Client, s.W.Company, s.Quote, Body(s.Version)); });
            var tb = Task.Run(() => { gate.Wait(); return AcceptAsync(round % 2 == 0 ? b.Client : a.Client, s.W.Company, s.Quote, Body(s.Version)); });
            gate.Set();
            var statuses = (await Task.WhenAll(ta, tb)).Select(r => r.StatusCode).ToList();
            statuses.Should().BeEquivalentTo([HttpStatusCode.Created, HttpStatusCode.Conflict], $"round {round}");
            (await OrdersOfAsync(s.Quote)).Should().Be(1);
        }
    }

    [PostgresFact]
    public async Task WithdrawRacingAnAccept_OverHttp_OneWins_AndNeverA500()
    {
        var buyer = await PersonAsync("buyer-a");
        var support = await _kit.StaffAsync(Permissions.Roles.Support);
        for (var round = 0; round < 4; round++)
        {
            var s = await SentQuoteAsync((buyer.Id, CompanyRoles.Buyer));
            using var gate = new ManualResetEventSlim(false);
            var accept = Task.Run(() => { gate.Wait(); return AcceptAsync(buyer.Client, s.W.Company, s.Quote, Body(s.Version)); });
            var withdraw = Task.Run(() => { gate.Wait(); return StaffQuoteKit.PostAsync(support.Client, QuoteIssueKit.Url(s.Quote, 1, "withdraw"), new { reason = "Supplier price went up" }); });
            gate.Set();
            var (accepted, withdrawn) = ((await accept).StatusCode, (await withdraw).StatusCode);
            new[] { accepted, withdrawn }.Should().BeEquivalentTo(accepted == HttpStatusCode.Created
                ? [HttpStatusCode.Created, HttpStatusCode.Conflict] : new[] { HttpStatusCode.Conflict, HttpStatusCode.OK }, $"round {round}");
            (await OrdersOfAsync(s.Quote)).Should().Be(accepted == HttpStatusCode.Created ? 1 : 0);
        }
    }
}
