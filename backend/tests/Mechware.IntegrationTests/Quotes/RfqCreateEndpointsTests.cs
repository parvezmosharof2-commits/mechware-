using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Catalog;
using Mechware.Domain.Companies;
using Mechware.Domain.Quotes;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.RfqApiKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.5 over HTTP (threat model QT10, QT17): the four sources, the part-number matching of the quick-order resolver, replay, the line and
/// size limits, plain-text storage, the audit and the outbox. Its own class (own host, own login budget).
/// </summary>
public sealed class RfqCreateEndpointsTests(RfqApiFactory factory) : IClassFixture<RfqApiFactory>
{
    private readonly RfqApiKit _kit = new(factory);

    /// <summary>Hex digits only: no O, I or L, so a random prefix never changes under the look-alike rule.</summary>
    private static string Prefix() => "R" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();

    private async Task<(Person Owner, Guid Company)> FirmAsync(string name)
    {
        var owner = await _kit.PersonAsync();
        return (owner, await _kit.CompanyAsync(owner.Id, name));
    }

    private static JsonElement Line(JsonElement rfq, int index) => rfq.GetProperty("lines")[index];

    [PostgresFact]
    public async Task RfqFromText_MatchedObsoleteAndUnmatchedLines_AreStoredAsTheDomainAllows_AndNothingIsSwappedOrPriced()
    {
        var (owner, company) = await FirmAsync("Rfq Text Ltd");
        var pre = Prefix();
        var matched = await _kit.VariantAsync(pre + "AAA1");
        var old = await _kit.VariantAsync(pre + "OLD1");
        var next = await _kit.VariantAsync(pre + "NEW1");
        await using (var db = _kit.Db())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE products SET seller_id = (SELECT seller_id FROM products WHERE id = {old.ProductId}) WHERE id = {next.ProductId}");
            var oldProduct = await db.Products.SingleAsync(p => p.Id == old.ProductId);
            oldProduct.SetSupply(SupplyStatus.Obsolete, await db.Products.SingleAsync(p => p.Id == next.ProductId), true, "Same footprint");
            await db.SaveChangesAsync();
        }

        var text = $"{pre}AAA1, 12\n{pre}OLD1 x 3\nZZ-NOSUCH-9 4\n{pre}AAA1 8\nDanfoss FC-302";
        var response = await PostAsync(owner.Client, company, new { source = "text", text, message = "For the new line", neededBy = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(20)) }, Key());
        ShouldBePrivate(response);
        var rfq = await DataAsync(response, HttpStatusCode.Created);

        rfq.GetProperty("rfq").Should().Match<JsonElement>(r => r.GetProperty("source").GetString() == "text" && r.GetProperty("status").GetString() == "submitted"
            && r.GetProperty("lineCount").GetInt32() == 4 && r.GetProperty("requestedByMe").GetBoolean());
        // The duplicate was merged (12 + 8), the obsolete part names its successor and is NOT replaced, the unknown ones are kept as asked.
        Line(rfq, 0).Should().Match<JsonElement>(l => l.GetProperty("matchStatus").GetString() == "matched" && l.GetProperty("quantity").GetInt32() == 20
            && l.GetProperty("variant").GetProperty("sku").GetString() == matched.Sku && l.GetProperty("note").GetString()!.StartsWith("Typed on lines"));
        Line(rfq, 1).Should().Match<JsonElement>(l => l.GetProperty("matchStatus").GetString() == "obsolete" && l.GetProperty("quantity").GetInt32() == 3
            && l.GetProperty("variant").GetProperty("sku").GetString() == old.Sku && l.GetProperty("successor").GetProperty("sku").GetString() == next.Sku);
        Line(rfq, 2).Should().Match<JsonElement>(l => l.GetProperty("matchStatus").GetString() == "not_found" && l.GetProperty("variant").ValueKind == JsonValueKind.Null
            && l.GetProperty("quantity").GetInt32() == 4 && l.GetProperty("rawText").GetString() == "ZZ-NOSUCH-9 4");
        Line(rfq, 3).GetProperty("matchStatus").GetString().Should().Be("not_found");

        // What the buyer must never see back, or send: prices, costs, staff, the idempotency data.
        var raw = rfq.GetRawText();
        foreach (var forbidden in new[] { "price", "cost", "assigned", "idempotency", "requestHash", "requestedBy\"" }) raw.Should().NotContainEquivalentOf(forbidden);

        await using var check = _kit.Db();
        var stored = await check.Rfqs.AsNoTracking().Include(r => r.Lines).SingleAsync(r => r.Id == rfq.GetProperty("rfq").GetProperty("id").GetGuid());
        stored.Should().Match<Rfq>(r => r.CompanyId == company && r.RequestedBy == owner.Id && r.Source == RfqSource.Text && r.LineCount == 4 && r.Status == RfqStatus.Submitted);
        stored.Lines.Select(l => l.MatchStatus).Should().Equal(RfqMatchStatus.Matched, RfqMatchStatus.Obsolete, RfqMatchStatus.NotFound, RfqMatchStatus.NotFound);
        (await check.OutboxMessages.CountAsync(o => o.EventType == nameof(RfqSubmittedEvent) && o.AggregateId == stored.Id)).Should().Be(1, "the staff notice is an outbox event");
        var audit = await check.AuditLogs.AsNoTracking().Where(a => a.Action == "rfq.submitted" && a.EntityId == stored.Id.ToString()).ToListAsync();
        audit.Should().ContainSingle().Which.ActorId.Should().Be(owner.Id);
    }

    [PostgresFact]
    public async Task RfqFromCsv_WithABomHeaderAndSemicolons_Works_AndIsLabelledCsv()
    {
        var (owner, company) = await FirmAsync("Rfq Csv Ltd");
        var pre = Prefix();
        var matched = await _kit.VariantAsync(pre + "CSV1");
        var csv = "﻿Part number;Qty\r\n" + $"{pre}CSV1;7\r\nSOMETHING-ELSE;2\r\n";

        var rfq = await DataAsync(await PostAsync(owner.Client, company, new { source = "CSV", text = csv }, Key()), HttpStatusCode.Created);
        rfq.GetProperty("rfq").GetProperty("source").GetString().Should().Be("csv");
        rfq.GetProperty("rfq").GetProperty("lineCount").GetInt32().Should().Be(2);
        Line(rfq, 0).Should().Match<JsonElement>(l => l.GetProperty("variant").GetProperty("sku").GetString() == matched.Sku && l.GetProperty("quantity").GetInt32() == 7);
        Line(rfq, 1).GetProperty("matchStatus").GetString().Should().Be("not_found");
    }

    [PostgresFact]
    public async Task RfqFromProduct_AsksForOneVariant_AndRefusesAnUnavailableOneWithoutSayingWhy()
    {
        var (owner, company) = await FirmAsync("Rfq Product Ltd");
        var variant = await _kit.VariantAsync(null);
        var rfq = await DataAsync(await PostAsync(owner.Client, company, new { source = "product", variantId = variant.VariantId, quantity = 250 }, Key()), HttpStatusCode.Created);
        rfq.GetProperty("rfq").GetProperty("source").GetString().Should().Be("product");
        Line(rfq, 0).Should().Match<JsonElement>(l => l.GetProperty("matchStatus").GetString() == "matched" && l.GetProperty("quantity").GetInt32() == 250
            && l.GetProperty("variant").GetProperty("sku").GetString() == variant.Sku);

        var unknown = await ErrorAsync(await PostAsync(owner.Client, company, new { source = "product", variantId = Guid.NewGuid(), quantity = 1 }, Key()), HttpStatusCode.UnprocessableEntity);
        unknown.Code.Should().Be("RFQ_LINES_INVALID");
        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "product", variantId = variant.VariantId, quantity = 10_001 }, Key()), HttpStatusCode.UnprocessableEntity)).Code.Should().Be("QTY_EXCEEDS_LIMIT");
        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "product", variantId = variant.VariantId, quantity = 0 }, Key()), HttpStatusCode.BadRequest)).Code.Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "product", quantity = 3 }, Key()), HttpStatusCode.BadRequest)).Code.Should().Be("VALIDATION_ERROR");
    }

    [PostgresAndRedisFact]
    public async Task RfqFromCart_ReadsTheServerCart_NotTheBody()
    {
        var (owner, company) = await FirmAsync("Rfq Cart Ltd");
        var inCart = await _kit.VariantAsync(null);
        var decoy = await _kit.VariantAsync(null);
        var added = await owner.Client.PostAsync("/api/v1/cart/items", new StringContent($$"""{ "variantId": "{{inCart.VariantId}}", "quantity": 3 }""", Encoding.UTF8, "application/json"));
        added.StatusCode.Should().Be(HttpStatusCode.OK);

        // The body tries every way of choosing lines: a text, a variant, a line list, prices, another company, another requester.
        var body = new
        {
            source = "cart",
            text = decoy.Sku + " 99",
            variantId = decoy.VariantId,
            quantity = 99,
            price = 1,
            unitPriceMinor = 1,
            companyId = Guid.NewGuid(),
            requestedBy = Guid.NewGuid(),
            lines = new[] { new { variantId = decoy.VariantId, quantity = 99, unitPriceMinor = 1 } }
        };
        var rfq = await DataAsync(await PostAsync(owner.Client, company, body, Key()), HttpStatusCode.Created);
        rfq.GetProperty("lines").GetArrayLength().Should().Be(1);
        Line(rfq, 0).Should().Match<JsonElement>(l => l.GetProperty("variant").GetProperty("id").GetGuid() == inCart.VariantId && l.GetProperty("quantity").GetInt32() == 3);
        rfq.GetProperty("rfq").GetProperty("source").GetString().Should().Be("cart");

        await using var check = _kit.Db();
        (await check.Rfqs.AsNoTracking().SingleAsync(r => r.Id == rfq.GetProperty("rfq").GetProperty("id").GetGuid())).Should().Match<Rfq>(r => r.CompanyId == company && r.RequestedBy == owner.Id);

        // The cart is not changed by asking for a quote, and an empty cart is refused.
        var cart = await DataAsync(await owner.Client.GetAsync("/api/v1/cart"));
        cart.GetProperty("lines").GetArrayLength().Should().Be(1);
        (await owner.Client.DeleteAsync("/api/v1/cart")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "cart" }, Key()), HttpStatusCode.UnprocessableEntity)).Code.Should().Be("RFQ_LINES_INVALID");
    }

    [PostgresFact]
    public async Task RfqReplay_WithTheSameKey_CreatesOne_AndAnotherBodyIsRefused()
    {
        var (owner, company) = await FirmAsync("Rfq Replay Ltd");
        var pre = Prefix();
        await _kit.VariantAsync(pre + "REP1");
        var key = Key();
        var body = new { source = "text", text = $"{pre}REP1 5", message = "Twice" };

        var first = await PostAsync(owner.Client, company, body, key);
        var created = await DataAsync(first, HttpStatusCode.Created);
        var second = await PostAsync(owner.Client, company, body, key);
        ShouldBePrivate(second);
        var replay = await DataAsync(second, HttpStatusCode.OK);
        replay.GetProperty("rfq").GetProperty("id").GetGuid().Should().Be(created.GetProperty("rfq").GetProperty("id").GetGuid());
        replay.GetProperty("rfq").GetProperty("number").GetString().Should().Be(created.GetProperty("rfq").GetProperty("number").GetString());

        // Same key, any different request: the message, the text, the source, the company.
        foreach (var other in new object[] { new { source = "text", text = $"{pre}REP1 5", message = "Other" }, new { source = "text", text = $"{pre}REP1 6", message = "Twice" },
                     new { source = "csv", text = $"{pre}REP1 5", message = "Twice" } })
            (await ErrorAsync(await PostAsync(owner.Client, company, other, key), HttpStatusCode.Conflict)).Code.Should().Be("IDEMPOTENCY_KEY_REUSED");

        // Control characters alone do not make a different request; a replay of the cleaned text is the same one.
        (await DataAsync(await PostAsync(owner.Client, company, new { source = "text", text = $"{pre}REP1 5", message = "Tw\u0000ice" }, key))).GetProperty("rfq").GetProperty("id").GetGuid()
            .Should().Be(created.GetProperty("rfq").GetProperty("id").GetGuid());

        await using var check = _kit.Db();
        (await check.Rfqs.CountAsync(r => r.RequestedBy == owner.Id && r.IdempotencyKey == key)).Should().Be(1);
        (await check.OutboxMessages.CountAsync(o => o.EventType == nameof(RfqSubmittedEvent) && o.AggregateId == created.GetProperty("rfq").GetProperty("id").GetGuid())).Should().Be(1, "a replay raises no second event");

        // The key is required and shaped.
        (await ErrorAsync(await PostAsync(owner.Client, company, body), HttpStatusCode.BadRequest)).Code.Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await PostAsync(owner.Client, company, body, "short"), HttpStatusCode.BadRequest)).Code.Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await PostAsync(owner.Client, company, body, "has spaces in the key"), HttpStatusCode.BadRequest)).Code.Should().Be("VALIDATION_ERROR");
    }

    [PostgresFact]
    public async Task AnRfqOverTheLineOrSizeLimit_IsRefused_WithPerLineErrors_AndNothingBrokenIsStored()
    {
        var (owner, company) = await FirmAsync("Rfq Limits Ltd");

        // 500 lines is the most; 501 is refused with the parser's reason.
        var five = string.Join('\n', Enumerable.Range(1, 500).Select(i => $"NOSUCH-{i:D4} {i % 9 + 1}"));
        var ok = await DataAsync(await PostAsync(owner.Client, company, new { source = "text", text = five }, Key()), HttpStatusCode.Created);
        ok.GetProperty("rfq").GetProperty("lineCount").GetInt32().Should().Be(500);
        var tooMany = await PostAsync(owner.Client, company, new { source = "text", text = five + "\nNOSUCH-0501 1" }, Key());
        ShouldBePrivate(tooMany);
        (await ErrorAsync(tooMany, HttpStatusCode.UnprocessableEntity)).Code.Should().Be("RFQ_LINES_INVALID");

        // Over 100,000 characters is refused as too large, before anything is parsed against the catalogue.
        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "csv", text = new string('A', 100_001) }, Key()), HttpStatusCode.RequestEntityTooLarge)).Code.Should().Be("INPUT_TOO_LARGE");

        // A body past the request limit never reaches the parser.
        var huge = await PostAsync(owner.Client, company, new { source = "text", text = new string('B', 600_000) }, Key());
        huge.StatusCode.Should().Be(HttpStatusCode.RequestEntityTooLarge);

        // One bad line refuses the whole RFQ and names the line; a quantity above the cap names its line too.
        var bad = await PostAsync(owner.Client, company, new { source = "text", text = "GOOD-1 2\nBAD-1, 2.5\nBAD-2,\nOVER-1 10001" }, Key());
        var text = await bad.Content.ReadAsStringAsync();
        bad.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity, text);
        var fields = JsonDocument.Parse(text).RootElement.GetProperty("error").GetProperty("details").EnumerateArray().Select(d => d.GetProperty("field").GetString()).ToList();
        fields.Should().Contain(["lines[2]", "lines[3]"]);
        var overQuantity = await ErrorAsync(await PostAsync(owner.Client, company, new { source = "text", text = "GOOD-1 2\nOVER-1 10001" }, Key()), HttpStatusCode.UnprocessableEntity);
        overQuantity.Code.Should().Be("QTY_EXCEEDS_LIMIT");
        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "text", text = "   \n# just a comment" }, Key()), HttpStatusCode.UnprocessableEntity)).Code.Should().Be("RFQ_LINES_INVALID");
        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "paper", text = "X 1" }, Key()), HttpStatusCode.BadRequest)).Code.Should().Be("VALIDATION_ERROR");

        await using var check = _kit.Db();
        (await check.Rfqs.CountAsync(r => r.CompanyId == company)).Should().Be(1, "only the 500-line request was stored");
    }

    [PostgresFact]
    public async Task RfqTextFields_AreStoredAsPlainText_AndControlCharactersAreStripped()
    {
        var (owner, company) = await FirmAsync("Rfq Plain Ltd");
        var pre = Prefix();
        await _kit.VariantAsync(pre + "TXT1");
        var message = "Need <script>alert(1)</script> by Sunday\u0000\u0007\r\nthen =HYPERLINK(\"http://x\")‮ fdp.exe";

        var rfq = await DataAsync(await PostAsync(owner.Client, company, new { source = "text", text = $"{pre}TXT1\u0000 x 4‮\nUnknown\u0001 part 3", message }, Key()), HttpStatusCode.Created);
        var stored = rfq.GetProperty("rfq").GetProperty("message").GetString()!;
        stored.Should().Be("Need <script>alert(1)</script> by Sunday\nthen =HYPERLINK(\"http://x\") fdp.exe", "markup is kept as text, controls and the bidi override are gone");
        Line(rfq, 1).GetProperty("rawText").GetString().Should().Be("Unknown part 3");

        await using var check = _kit.Db();
        var id = rfq.GetProperty("rfq").GetProperty("id").GetGuid();
        var row = await check.Rfqs.AsNoTracking().Include(r => r.Lines).SingleAsync(r => r.Id == id);
        row.Message.Should().Be(stored);
        string.Concat(row.Message, string.Concat(row.Lines.Select(l => l.RawText + l.PartKey + l.Note))).Any(c => char.IsControl(c) && c != '\n' && c != '\t').Should().BeFalse();
    }

    [PostgresFact]
    public async Task Withdraw_MovesAnOpenRfqToWithdrawn_AuditsTheActor_AndAClosedOneIsRefused()
    {
        var (owner, company) = await FirmAsync("Rfq Withdraw Ltd");
        var variant = await _kit.VariantAsync(null);
        var open = await DataAsync(await PostAsync(owner.Client, company, new { source = "product", variantId = variant.VariantId, quantity = 1 }, Key()), HttpStatusCode.Created);
        var id = open.GetProperty("rfq").GetProperty("id").GetGuid();

        var withdrawn = await owner.Client.PostAsync(Url(company, $"/{id}/withdraw"), null);
        ShouldBePrivate(withdrawn);
        (await DataAsync(withdrawn)).GetProperty("rfq").Should().Match<JsonElement>(r => r.GetProperty("status").GetString() == "withdrawn" && r.GetProperty("withdrawnAt").ValueKind == JsonValueKind.String);
        // Withdrawing again is one withdrawal, not an error and not a second audit row.
        (await DataAsync(await owner.Client.PostAsync(Url(company, $"/{id}/withdraw"), null))).GetProperty("rfq").GetProperty("status").GetString().Should().Be("withdrawn");

        // Staff closed one (Closed with a reason the buyer sees): the buyer cannot withdraw it any more.
        var closedId = (await DataAsync(await PostAsync(owner.Client, company, new { source = "product", variantId = variant.VariantId, quantity = 2 }, Key()), HttpStatusCode.Created))
            .GetProperty("rfq").GetProperty("id").GetGuid();
        await using (var db = _kit.Db())
        {
            var rfq = await db.Rfqs.Include(r => r.Lines).SingleAsync(r => r.Id == closedId);
            rfq.Close(Guid.NewGuid(), "No bid on this item", DateTime.UtcNow);
            await db.SaveChangesAsync();
        }

        (await ErrorAsync(await owner.Client.PostAsync(Url(company, $"/{closedId}/withdraw"), null), HttpStatusCode.Conflict)).Code.Should().Be("RFQ_INVALID_TRANSITION");
        var shown = await DataAsync(await owner.Client.GetAsync(Url(company, "/" + closedId)));
        shown.GetProperty("rfq").Should().Match<JsonElement>(r => r.GetProperty("status").GetString() == "closed" && r.GetProperty("closeReason").GetString() == "No bid on this item");

        await using var check = _kit.Db();
        var audit = await check.AuditLogs.AsNoTracking().Where(a => a.Action == "rfq.withdrawn" && a.EntityId == id.ToString()).ToListAsync();
        audit.Should().ContainSingle().Which.ActorId.Should().Be(owner.Id, "the table has no actor column, so the audit row is the record");
        (await check.Rfqs.AsNoTracking().SingleAsync(r => r.Id == closedId)).Status.Should().Be(RfqStatus.Closed);
    }

    [PostgresFact]
    public async Task ASiteOfAnotherCompany_AndAPastDate_AreRefused()
    {
        var (owner, company) = await FirmAsync("Rfq Site Ltd");
        var (otherOwner, otherCompany) = await FirmAsync("Rfq Other Site Ltd");
        var variant = await _kit.VariantAsync(null);
        Guid foreignSite;
        await using (var db = _kit.Db())
        {
            var site = new CompanySite(Guid.NewGuid(), otherCompany, "Plant", "Contact", "+8801711000000", "Road 1", null, "Tongi", "Gazipur", "Dhaka", null, true);
            db.CompanySites.Add(site);
            await db.SaveChangesAsync();
            foreignSite = site.Id;
        }

        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "product", variantId = variant.VariantId, quantity = 1, shipToSiteId = foreignSite }, Key()), HttpStatusCode.BadRequest)).Code.Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await PostAsync(owner.Client, company, new { source = "product", variantId = variant.VariantId, quantity = 1, neededBy = "2020-01-01" }, Key()), HttpStatusCode.BadRequest)).Code.Should().Be("VALIDATION_ERROR");
        _ = otherOwner;
    }
}
