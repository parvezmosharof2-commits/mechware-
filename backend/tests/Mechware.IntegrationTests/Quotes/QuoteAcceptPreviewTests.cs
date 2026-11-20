using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Orders;
using Mechware.Application.Quotes;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.IntegrationTests.Pricing;
using Mechware.IntegrationTests.Support;
using static Mechware.IntegrationTests.Quotes.QuoteAcceptKit;

namespace Mechware.IntegrationTests.Quotes;

/// <summary>
/// D4.10, the accept preview against a real database, service level (ADR-0011 §8.4; threat model QT15): it prices through the acceptance's
/// own path (<see cref="Infrastructure.Orders.OrderService.PreviewQuoteAcceptanceAsync"/> shares the checks and the quote pricing with
/// <see cref="Infrastructure.Orders.OrderService.PlaceFromQuoteAsync"/>), so the order accepted at the same instant is exactly the preview; it
/// holds no stock and writes nothing, refuses what acceptance refuses, and tells the buyer about a new VAT rate, a lower own price and short stock.
/// </summary>
public sealed class QuoteAcceptPreviewTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private readonly QuoteAcceptKit _kit = new(fixture);

    private async Task<QuoteAcceptPreviewDto> PreviewAsync(Team t, Guid quote, int n = 1, Guid? buyer = null, bool onCredit = true, string? hash = null,
        DateTime? at = null, Guid? site = null, Guid? company = null)
    {
        var v = await _kit.VersionAsync(quote, n);
        await using var db = _kit.Db();
        var who = buyer ?? t.Buyer;
        return await _kit.Orders(db, who, at).PreviewQuoteAcceptanceAsync(new AcceptQuoteRequest(who, string.Empty, company ?? t.Company, quote, n,
            hash ?? v.ContentHash, null, onCredit, site));
    }

    private async Task<Guid> SiteAsync(Team t)
    {
        await using var db = _kit.Db();
        var site = new Domain.Companies.CompanySite(Guid.NewGuid(), t.Company, "Tongi works", "Selim Reza", "01911000000", "Station Road 12", null, "Tongi",
            "Gazipur", "Dhaka", "1710", false);
        db.CompanySites.Add(site);
        await db.SaveChangesAsync();
        return site.Id;
    }

    private static void ShouldEqual(QuoteAcceptPreviewDto preview, Domain.Orders.Order order)
    {
        (order.SubtotalMinor, order.TaxMinor, order.ShippingMinor, order.GrandTotalMinor)
            .Should().Be((preview.SubtotalMinor, preview.TaxMinor, preview.ShippingMinor, preview.GrandTotalMinor));
        order.SubOrders.SelectMany(s => s.Items).Select(i => new
        {
            QuoteLineId = i.QuoteLineId!.Value,
            i.VariantId,
            i.Quantity,
            i.UnitPriceMinor,
            i.VatRate,
            i.NetMinor,
            i.TaxMinor,
            i.TotalMinor
        }).Should().BeEquivalentTo(preview.Lines.Select(l => new
        {
            l.QuoteLineId,
            l.VariantId,
            l.Quantity,
            l.UnitPriceMinor,
            l.VatRate,
            l.NetMinor,
            l.TaxMinor,
            l.TotalMinor
        }));
    }

    [PostgresFact]
    public async Task AcceptPreview_EqualsTheAcceptedOrder()
    {
        var at = PriceListTestData.WholeSecond(DateTime.UtcNow);

        // Calculated delivery, the VAT rate of one line raised after the send: the preview is the server's total now, not the quote's.
        var t = await _kit.TeamAsync();
        var (bearing, belt) = (await _kit.ItemAsync(), await _kit.ItemAsync(grossMinor: 52_500));
        var quote = await _kit.SentAsync(t, [new Line(bearing, 4, bearing.Net - 100), new Line(belt, 3)]);
        await _kit.SqlAsync($"UPDATE categories SET vat_rate = 0.10 WHERE id = {bearing.Category}");
        var preview = await PreviewAsync(t, quote, at: at);
        preview.GrandTotalMinor.Should().BeGreaterThan((await _kit.VersionAsync(quote)).GrandTotalMinor ?? (await _kit.VersionAsync(quote)).GoodsTotalMinor);
        (preview.VatRateChanged, preview.Lines.Single(l => l.VariantId == bearing.Variant).VatRateChanged, preview.Lines.Single(l => l.VariantId == belt.Variant).VatRateChanged)
            .Should().Be((true, true, false));
        var placed = await _kit.AcceptAsync(t, quote, onCredit: true, expected: preview.GrandTotalMinor, at: at);
        placed.GrandTotalMinor.Should().Be(preview.GrandTotalMinor);
        ShouldEqual(preview, await _kit.OrderAsync(placed.OrderId));

        // Fixed delivery: the quote's fee per seller, to its site.
        var fixedTeam = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var fixedQuote = await _kit.SentAsync(fixedTeam, [new Line(item, 3)], delivery: new FixedDelivery(await SiteAsync(fixedTeam), 6_500));
        var fixedPreview = await PreviewAsync(fixedTeam, fixedQuote, at: at);
        (fixedPreview.ShippingMinor, fixedPreview.VatRateChanged, fixedPreview.LowerPriceNow, fixedPreview.StockShort).Should().Be((6_500L, false, false, false));
        fixedPreview.GrandTotalMinor.Should().Be((await _kit.VersionAsync(fixedQuote)).GrandTotalMinor);
        var fixedOrder = await _kit.AcceptAsync(fixedTeam, fixedQuote, onCredit: true, expected: fixedPreview.GrandTotalMinor, at: at);
        ShouldEqual(fixedPreview, await _kit.OrderAsync(fixedOrder.OrderId));
    }

    [PostgresFact]
    public async Task AcceptPreview_HoldsNoStock_AndWritesNothing()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync(onHand: 10);
        var quote = await _kit.SentAsync(t, [new Line(item, 4)]);
        int Count(Infrastructure.Persistence.MechwareDbContext db) => db.AuditLogs.Count() + db.OutboxMessages.Count() + db.IdempotencyKeys.Count() + db.Orders.Count();
        int before;
        await using (var db = _kit.Db()) before = Count(db);

        for (var i = 0; i < 3; i++) (await PreviewAsync(t, quote)).Lines.Single().Quantity.Should().Be(4);

        (await _kit.StockAsync(item)).Should().Be((10, 0));
        await using (var db = _kit.Db()) Count(db).Should().Be(before, "no audit row, event, key or order");
        (await _kit.VersionAsync(quote)).Status.Should().Be(Domain.Quotes.QuoteVersionStatus.Sent);
    }

    [PostgresFact]
    public async Task AcceptPreview_RefusesWhatAcceptanceRefuses()
    {
        var t = await _kit.TeamAsync();
        var item = await _kit.ItemAsync();
        var advance = await _kit.SentAsync(t, [new Line(item)], terms: "advance");
        (await CodeOf(() => PreviewAsync(t, advance, onCredit: true))).Should().Be("QUOTE_REQUIRES_ADVANCE_PAYMENT");
        (await PreviewAsync(t, advance, onCredit: false)).Lines.Should().ContainSingle();

        var quote = await _kit.SentAsync(t, [new Line(item)], validityDays: 1);
        (await CodeOf(() => PreviewAsync(t, quote, hash: new string('0', 64)))).Should().Be("QUOTE_CHANGED");
        (await CodeOf(() => PreviewAsync(t, quote, at: DateTime.UtcNow.AddDays(3)))).Should().Be("QUOTE_NOT_ACCEPTABLE", "expired");

        var site = await SiteAsync(t);
        var fixedQuote = await _kit.SentAsync(t, [new Line(item)], delivery: new FixedDelivery(site, 4_000));
        var otherSite = await SiteAsync(t);
        (await CodeOf(() => PreviewAsync(t, fixedQuote, site: otherSite))).Should().Be("QUOTE_DELIVERY_FIXED");
        await _kit.SqlAsync($"UPDATE products SET status = 'archived' WHERE id = (SELECT product_id FROM product_variants WHERE id = {item.Variant})");
        (await CodeOf(() => PreviewAsync(t, quote))).Should().Be("VARIANT_UNAVAILABLE", "the catalogue is checked again, as at acceptance");

        var other = await _kit.TeamAsync();
        foreach (var (company, buyer) in new[] { (other.Company, t.Buyer), (t.Company, other.Buyer), (t.Company, t.Approver) })
            await FluentActions.Awaiting(() => PreviewAsync(t, quote, buyer: buyer, company: company)).Should().ThrowAsync<KeyNotFoundException>().WithMessage("Quote not found.");
    }

    [PostgresFact]
    public async Task AcceptPreview_TellsOfALowerOwnPriceNow_AndOfShortStock()
    {
        var t = await _kit.TeamAsync();
        var (contracted, scarce) = (await _kit.ItemAsync(), await _kit.ItemAsync(onHand: 3));
        var quote = await _kit.SentAsync(t, [new Line(contracted, 2), new Line(scarce, 2)]);
        await _kit.SqlAsync($"UPDATE inventory_items SET reserved = 2 WHERE id = {scarce.InventoryItem}");   // held by another order: 1 free
        await using (var lists = _kit.Db())
        {
            var from = DateTime.UtcNow.AddMinutes(-5);
            await PriceListTestData.ApprovedAsync(lists, PriceListScope.Company, t.Company, from, from.AddDays(30), from,
                new PriceListTestData.Entry(contracted.Variant, 1, contracted.Net - 1_000));
        }

        var preview = await PreviewAsync(t, quote);
        var byVariant = preview.Lines.ToDictionary(l => l.VariantId);
        (byVariant[contracted.Variant].LowerPriceNow, byVariant[contracted.Variant].StockShort, byVariant[contracted.Variant].UnitPriceMinor)
            .Should().Be((true, false, contracted.Net), "the quoted price still binds; the buyer is only told");
        (byVariant[scarce.Variant].LowerPriceNow, byVariant[scarce.Variant].StockShort).Should().Be((false, true));
        (preview.LowerPriceNow, preview.StockShort).Should().Be((true, true));
        (await CodeOf(() => _kit.AcceptAsync(t, quote, onCredit: true, expected: preview.GrandTotalMinor))).Should().Be("INSUFFICIENT_STOCK");
    }
}
