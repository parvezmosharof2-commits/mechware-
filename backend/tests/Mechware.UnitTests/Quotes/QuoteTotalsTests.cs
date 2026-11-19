using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Properties.PropertyRunner;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>D4.3: line and version totals of ADR-0011 §4 are the order's money (<see cref="OrderPricing"/>, VAT once per line, ADR-0002).</summary>
public class QuoteTotalsTests
{
    private static readonly decimal[] Vats = [0m, 0.05m, 0.075m, 0.1m, 0.15m, 0.1234m, 0.5m];

    [Fact]
    public void QuoteTotals_EqualOrderPricing_ToThePoisha()
    {
        ForAll(4304, 2_000, (rng, _) =>
        {
            var sellers = Enumerable.Range(0, rng.Next(1, 5)).Select(_ => Guid.NewGuid()).ToArray();
            var lines = Enumerable.Range(0, rng.Next(1, QuoteLimits.MaxLinesPerVersion + 1))
                .Select(_ => new QuoteTotalsLine(Guid.NewGuid(), Guid.NewGuid(), sellers[rng.Next(sellers.Length)], rng.Next(1, 10_001),
                    rng.Next(3) == 0 ? rng.NextInt64(1, 100) : rng.NextInt64(1, 100_000_000), Vats[rng.Next(Vats.Length)]))
                .ToList();
            var used = lines.Select(l => l.SellerId).Distinct().ToList();
            var isFixed = rng.Next(2) == 0;
            var fees = used.Select(s => new QuoteShippingFee(s, rng.NextInt64(0, 5_000_000))).ToList();

            var totals = QuoteTotals.Compute(lines, isFixed ? QuoteShippingMode.Fixed : QuoteShippingMode.Calculated, isFixed ? fees : null);

            var order = OrderPricing.Price(
                lines.Select(l => new OrderLineDraft(l.LineId, l.VariantId, l.SellerId, "SKU", "Item", l.Quantity, l.UnitPriceMinor, l.VatRate, 0)).ToList(),
                used.Select(s => new SellerTerms(s, isFixed ? fees.Single(f => f.SellerId == s).ShippingMinor : 0, 0.1m)).ToList(), TaxMode.Exclusive);
            var priced = order.SubOrders.SelectMany(s => s.Lines).ToDictionary(l => l.LineId);
            foreach (var l in lines)
                totals.Lines[l.LineId].Should().Be(new QuoteLineTotals(priced[l.LineId].NetMinor, priced[l.LineId].TaxMinor, priced[l.LineId].TotalMinor));
            totals.GoodsNetMinor.Should().Be(order.SubOrders.Sum(s => s.GoodsNetMinor));
            totals.TaxMinor.Should().Be(order.TaxMinor);
            totals.GoodsTotalMinor.Should().Be(order.GrandTotalMinor - order.ShippingMinor).And.Be(totals.GoodsNetMinor + totals.TaxMinor);
            totals.ShippingTotalMinor.Should().Be(isFixed ? order.ShippingMinor : null);
            totals.GrandTotalMinor.Should().Be(isFixed ? order.GrandTotalMinor : null);
        });
    }

    [Fact]
    public void VatIsRoundedOncePerLine_HalfToEven_NotOnTheVersionTotal()
    {
        // Three lines of 1 × 10 poisha at 15 %: 1.5 → 2 per line, so 6, where 15 % of 30 would be 4.5 → 4.
        var lines = Enumerable.Range(0, 3).Select(_ => new QuoteTotalsLine(Guid.NewGuid(), Guid.NewGuid(), Seller1, 1, 10, 0.15m)).ToList();
        var t = QuoteTotals.Compute(lines, QuoteShippingMode.Calculated, null);
        (t.GoodsNetMinor, t.TaxMinor, t.GoodsTotalMinor).Should().Be((30L, 6L, 36L));

        // 10 at 25 %: 2.5 → 2 (banker's), and 30 at 25 %: 7.5 → 8.
        QuoteTotals.Compute([new(Guid.NewGuid(), Guid.NewGuid(), Seller1, 1, 10, 0.25m)], QuoteShippingMode.Calculated, null).TaxMinor.Should().Be(2);
        QuoteTotals.Compute([new(Guid.NewGuid(), Guid.NewGuid(), Seller1, 3, 10, 0.25m)], QuoteShippingMode.Calculated, null).TaxMinor.Should().Be(8);
    }

    [Fact]
    public void ShippingAndGrandTotal_OnlyWhenDeliveryIsFixed_ForEverySeller()
    {
        var lines = new QuoteTotalsLine[] { new(Guid.NewGuid(), V1, Seller1, 2, 1_000, 0.15m), new(Guid.NewGuid(), V2, Seller2, 1, 500, 0m) };
        var all = QuoteTotals.Compute(lines, QuoteShippingMode.Fixed, [new(Seller1, 300), new(Seller2, 0)]);
        (all.GoodsNetMinor, all.TaxMinor, all.GoodsTotalMinor, all.ShippingTotalMinor, all.GrandTotalMinor).Should().Be((2_500L, 300L, 2_800L, 300L, 3_100L));

        // A draft may still miss a fee: no shipping and no grand total yet (the send refuses it).
        var partial = QuoteTotals.Compute(lines, QuoteShippingMode.Fixed, [new(Seller1, 300)]);
        (partial.ShippingTotalMinor, partial.GrandTotalMinor, partial.GoodsTotalMinor).Should().Be(((long?)null, (long?)null, 2_800L));
        QuoteTotals.Compute(lines, QuoteShippingMode.Calculated, null).GrandTotalMinor.Should().BeNull();
        QuoteTotals.Compute([], QuoteShippingMode.Calculated, null).GoodsTotalMinor.Should().Be(0);
        FluentActions.Invoking(() => QuoteTotals.Compute(lines, QuoteShippingMode.Fixed, [new(Seller1, 300), new(Seller1, 400)]))
            .Should().Throw<ValidationException>();
        FluentActions.Invoking(() => QuoteTotals.Compute([lines[0] with { VatRate = 0.6m }], QuoteShippingMode.Calculated, null))
            .Should().Throw<ValidationException>("a rate the order path refuses can never be quoted");
    }

    [Fact]
    public void TheVersion_CarriesItsTotals_AndTheSendUsesTheVatRateOfThatMoment()
    {
        var quote = Drafted(); // 10 × 100,000 and 4 × 250,000, both at 15 %
        var v = quote.Version(1);
        (v.GoodsNetMinor, v.TaxMinor, v.GoodsTotalMinor, v.ShippingTotalMinor, v.GrandTotalMinor)
            .Should().Be((2_000_000L, 300_000L, 2_300_000L, (long?)null, (long?)null), "a draft shows its totals while it is edited");

        var issue = Issue(quote);
        quote.Send(1, issue with { Facts = [issue.Facts[0] with { VatRate = 0.075m }, issue.Facts[1]] }, Staff(Sender), false, Options, Now);
        v.Lines.Select(l => (l.LineNetMinor, l.LineTaxMinor, l.LineTotalMinor)).Should().Equal((1_000_000L, 75_000L, 1_075_000L), (1_000_000L, 150_000L, 1_150_000L));
        (v.GoodsNetMinor, v.TaxMinor, v.GoodsTotalMinor).Should().Be((2_000_000L, 225_000L, 2_225_000L));
    }

    [Fact]
    public void AFixedDeliveryVersion_HasShippingAndAGrandTotal()
    {
        var quote = Drafted();
        quote.EditDraft(1, Content(Line(V1, seller: Seller1), Line(V2, qty: 4, price: 250_000, seller: Seller2)) with
        {
            ShippingMode = QuoteShippingMode.Fixed,
            ShipToSiteId = Site,
            ShippingFees = [new(Seller1, 5_000), new(Seller2, 1_500)],
        }, Staff(Author), Options, Now);
        quote.Send(1, Issue(quote), Staff(Sender), false, Options, Now);
        quote.Version(1).Should().Match<QuoteVersion>(v => v.ShippingTotalMinor == 6_500 && v.GrandTotalMinor == 2_306_500 && v.GoodsTotalMinor == 2_300_000);
    }
}
