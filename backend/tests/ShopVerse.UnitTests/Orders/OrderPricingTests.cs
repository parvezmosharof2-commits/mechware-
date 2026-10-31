using FluentAssertions;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Orders;
using ShopVerse.Domain.Pricing;

namespace ShopVerse.UnitTests.Orders;

public class OrderPricingTests
{
    private static readonly Guid SellerA = Guid.Parse("a1000000-0000-0000-0000-00000000000a");
    private static readonly Guid SellerB = Guid.Parse("b1000000-0000-0000-0000-00000000000b");

    private static OrderLineDraft Line(int n, Guid seller, int qty, long unit, decimal vat = 0.05m, long discount = 0) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), Guid.Parse($"11111111-0000-0000-0000-{n:D12}"), seller, $"SKU-{n}", $"Item {n}", qty, unit, vat, discount);

    private static SellerTerms Terms(Guid seller, long shipping = 0, decimal commission = 0.08m) => new(seller, shipping, commission);

    private static void AssertReconciles(PricedOrder o, TaxMode mode = TaxMode.Exclusive)
    {
        foreach (var s in o.SubOrders)
        {
            s.TotalMinor.Should().Be(s.Lines.Sum(l => l.TotalMinor) + s.ShippingMinor);
            s.CommissionMinor.Should().Be(s.GoodsNetMinor - s.SellerEarningMinor);
            s.SellerEarningMinor.Should().BeGreaterThanOrEqualTo(0);
            foreach (var l in s.Lines)
            {
                l.TotalMinor.Should().Be(l.NetMinor + l.TaxMinor);
                if (mode == TaxMode.Exclusive) l.NetMinor.Should().Be(l.GrossMinor - l.DiscountMinor, "exclusive: VAT is added on top of the discounted price");
                else l.TotalMinor.Should().Be(l.GrossMinor - l.DiscountMinor, "inclusive: the buyer pays exactly the discounted price, VAT is inside it");
            }
        }

        o.GrandTotalMinor.Should().Be(o.SubOrders.Sum(s => s.TotalMinor));
        o.GrandTotalMinor.Should().Be(o.SubOrders.SelectMany(s => s.Lines).Sum(l => l.TotalMinor) + o.ShippingMinor);
        o.TaxMinor.Should().Be(o.SubOrders.SelectMany(s => s.Lines).Sum(l => l.TaxMinor));
    }

    [Fact]
    public void Exclusive_AddsVatOnTopOfTheDiscountedNet()
    {
        // 2 x 1,000.00 = 2,000.00; 100.00 off -> 1,900.00 net; 5 % VAT = 95.00; total 1,995.00.
        var o = OrderPricing.Price([Line(1, SellerA, 2, 100_000, discount: 10_000)], [Terms(SellerA)], TaxMode.Exclusive);
        var l = o.SubOrders.Single().Lines.Single();
        (l.GrossMinor, l.NetMinor, l.TaxMinor, l.TotalMinor).Should().Be((200_000, 190_000, 9_500, 199_500));
        o.GrandTotalMinor.Should().Be(199_500);
    }

    [Fact]
    public void Inclusive_SplitsTheVatOutOfThePrice_NeverAddsItTwice()
    {
        // 1,050.00 inclusive of 5 % -> net 1,000.00, tax 50.00, total stays 1,050.00.
        var o = OrderPricing.Price([Line(1, SellerA, 1, 105_000)], [Terms(SellerA)], TaxMode.Inclusive);
        var l = o.SubOrders.Single().Lines.Single();
        (l.NetMinor, l.TaxMinor, l.TotalMinor).Should().Be((100_000, 5_000, 105_000));
        o.GrandTotalMinor.Should().Be(105_000);
    }

    [Fact]
    public void ALinesVatIsRoundedOnce_InWholePoisha_ByBankersRounding()
    {
        // net 25 poisha x 10 % = 2.5 -> 2 (even); net 35 x 10 % = 3.5 -> 4.
        PricedOrder Tax(long unit) => OrderPricing.Price([Line(1, SellerA, 1, unit, vat: 0.10m)], [Terms(SellerA)], TaxMode.Exclusive);
        Tax(25).TaxMinor.Should().Be(2);
        Tax(35).TaxMinor.Should().Be(4);
    }

    [Fact]
    public void LinesAreSplitIntoOneSubOrderPerSeller_WithOwnShippingAndCommission()
    {
        var o = OrderPricing.Price(
            [Line(1, SellerA, 1, 100_000), Line(2, SellerB, 2, 50_000), Line(3, SellerA, 1, 20_000)],
            [Terms(SellerA, shipping: 15_000, commission: 0.10m), Terms(SellerB, shipping: 0, commission: 0.05m)],
            TaxMode.Exclusive);

        o.SubOrders.Select(s => s.SellerId).Should().Equal(SellerA, SellerB);
        var a = o.SubOrders[0];
        a.Lines.Should().HaveCount(2);
        (a.GoodsNetMinor, a.CommissionMinor, a.SellerEarningMinor, a.ShippingMinor).Should().Be((120_000, 12_000, 108_000, 15_000));
        a.TotalMinor.Should().Be(120_000 + 6_000 + 15_000);
        var b = o.SubOrders[1];
        (b.GoodsNetMinor, b.CommissionMinor, b.SellerEarningMinor).Should().Be((100_000, 5_000, 95_000));
        o.ShippingMinor.Should().Be(15_000);
        AssertReconciles(o);
    }

    [Fact]
    public void Commission_IsOnTheDiscountedNetGoods_NotOnTheTaxOrShipping()
    {
        var o = OrderPricing.Price([Line(1, SellerA, 1, 100_000, discount: 20_000)], [Terms(SellerA, shipping: 9_999, commission: 0.10m)], TaxMode.Exclusive);
        o.SubOrders.Single().CommissionMinor.Should().Be(8_000);   // 10 % of 80,000
    }

    [Fact]
    public void AClientCannotInfluenceTheTotals_OnlyTheServerResolvedInputsCount()
    {
        // The draft has no field for a client-supplied total, price or tax: tampering has nowhere to go. A discount larger
        // than the line, a negative price or an impossible VAT rate is refused outright.
        ((Action)(() => OrderPricing.Price([Line(1, SellerA, 1, 1_000, discount: 1_001)], [Terms(SellerA)], TaxMode.Exclusive))).Should().Throw<ValidationException>();
        ((Action)(() => OrderPricing.Price([Line(1, SellerA, 1, -1)], [Terms(SellerA)], TaxMode.Exclusive))).Should().Throw<ValidationException>();
        ((Action)(() => OrderPricing.Price([Line(1, SellerA, 1, 1_000, vat: 0.9m)], [Terms(SellerA)], TaxMode.Exclusive))).Should().Throw<ValidationException>();
        ((Action)(() => OrderPricing.Price([Line(1, SellerA, 0, 1_000)], [Terms(SellerA)], TaxMode.Exclusive))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void MissingOrDuplicateSellerTerms_AndEmptyOrders_AreRefused()
    {
        ((Action)(() => OrderPricing.Price([Line(1, SellerB, 1, 1_000)], [Terms(SellerA)], TaxMode.Exclusive))).Should().Throw<ValidationException>();
        ((Action)(() => OrderPricing.Price([Line(1, SellerA, 1, 1_000)], [Terms(SellerA), Terms(SellerA)], TaxMode.Exclusive))).Should().Throw<ValidationException>();
        ((Action)(() => OrderPricing.Price([], [Terms(SellerA)], TaxMode.Exclusive))).Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData(TaxMode.Exclusive)]
    [InlineData(TaxMode.Inclusive)]
    public void RandomOrders_AlwaysReconcileToThePoisha(TaxMode mode)
    {
        var rng = new Random(20261101);
        for (var round = 0; round < 400; round++)
        {
            var sellers = new[] { SellerA, SellerB, Guid.Parse("c1000000-0000-0000-0000-00000000000c") };
            var lines = Enumerable.Range(1, rng.Next(1, 9)).Select(n =>
            {
                var qty = rng.Next(1, 30);
                var unit = rng.Next(0, 3_000_000);
                var gross = (long)qty * unit;
                return Line(n, sellers[rng.Next(sellers.Length)], qty, unit, (decimal)rng.Next(0, 16) / 100m, gross == 0 ? 0 : rng.NextInt64(0, gross + 1));
            }).ToList();
            var terms = sellers.Select(s => Terms(s, rng.Next(0, 50_000), (decimal)rng.Next(0, 31) / 100m)).ToList();

            var o = OrderPricing.Price(lines, terms, mode);

            AssertReconciles(o, mode);
            o.SubtotalMinor.Should().Be(lines.Sum(l => (long)l.Quantity * l.UnitPriceMinor));
            o.DiscountMinor.Should().Be(lines.Sum(l => l.DiscountMinor));
        }
    }
}
