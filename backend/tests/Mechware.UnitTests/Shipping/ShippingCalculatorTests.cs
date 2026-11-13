using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Shipping;

namespace Mechware.UnitTests.Shipping;

public class ShippingCalculatorTests
{
    private static readonly Guid District = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid OtherDistrict = Guid.Parse("d0000000-0000-0000-0000-000000000002");
    private static readonly Guid SellerA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid SellerB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    // Test data only: these are NOT real tariffs. Bands: up to 1 kg 100, up to 5 kg 250, up to 20 kg 700.
    private static ShippingRateCard Card(long? free = 100_000, int? heavy = 15_000, int? oversize = 1_200, long max = 1_000) =>
        new([new WeightBand(1_000, 100), new WeightBand(5_000, 250), new WeightBand(20_000, 700)], free, heavy, oversize, max);

    private static ShippingZoneConfig Zone(ShippingRateCard? card = null, params Guid[] districts) =>
        new(Guid.NewGuid(), "test", (districts.Length == 0 ? [District] : districts).ToHashSet(), card ?? Card());

    private static ShippingLine Line(Guid seller, int qty, int grams, int? l = null, int? w = null, int? h = null) =>
        new(Guid.NewGuid(), seller, qty, grams, l, w, h);

    private static ShippingQuote.Priced Priced(ShippingZoneConfig zone, long subtotal, params ShippingLine[] lines) =>
        ShippingCalculator.Calculate([zone], District, lines, subtotal).Should().BeOfType<ShippingQuote.Priced>().Subject;

    [Theory]
    [InlineData(1, 100)]
    [InlineData(999, 100)]
    [InlineData(1_000, 100)] // exactly maxKg stays in the band
    [InlineData(1_001, 250)] // maxKg + 0.001 kg moves to the next band
    [InlineData(5_000, 250)]
    [InlineData(5_001, 700)]
    [InlineData(20_000, 700)]
    public void Weight_rounds_up_into_the_next_band_at_the_boundary(int grams, long expectedFee)
    {
        var quote = Priced(Zone(Card(heavy: null)), 1, Line(SellerA, 1, grams));
        quote.TotalMinor.Should().Be(expectedFee);
        quote.PerSeller.Single().ShippingMinor.Should().Be(expectedFee);
    }

    [Fact]
    public void Above_the_last_band_is_quote_only_not_a_guess()
    {
        var quote = ShippingCalculator.Calculate([Zone(Card(heavy: null))], District, [Line(SellerA, 1, 20_001)], 1);
        quote.Should().BeOfType<ShippingQuote.QuoteOnly>().Which.Reasons
            .Should().ContainSingle(r => r.Code == ShippingCalculator.AboveLastBand && r.SellerId == SellerA);
    }

    [Theory]
    [InlineData(99_999, 100)]
    [InlineData(100_000, 0)] // subtotal == threshold ships free
    [InlineData(100_001, 0)]
    public void Free_shipping_threshold_boundary(long subtotal, long expectedFee)
    {
        var quote = Priced(Zone(), subtotal, Line(SellerA, 1, 500));
        quote.TotalMinor.Should().Be(expectedFee);
        quote.FreeShippingApplied.Should().Be(expectedFee == 0);
    }

    [Fact]
    public void Heavy_unit_makes_the_whole_order_quote_only_with_reasons()
    {
        var heavy = Line(SellerA, 1, 15_001);
        var quote = ShippingCalculator.Calculate([Zone()], District, [Line(SellerB, 1, 200), heavy], 500_000);
        var reasons = quote.Should().BeOfType<ShippingQuote.QuoteOnly>().Subject.Reasons;
        reasons.Should().ContainSingle(r => r.Code == ShippingCalculator.HeavyUnit && r.VariantId == heavy.VariantId && r.SellerId == SellerA);
    }

    [Fact]
    public void Heavy_boundary_is_strictly_greater_than()
    {
        Priced(Zone(), 1, Line(SellerA, 1, 15_000)).TotalMinor.Should().Be(700);
    }

    [Theory]
    [InlineData(1_201, null, null)]
    [InlineData(null, 1_201, null)]
    [InlineData(null, null, 1_201)]
    public void Oversize_side_makes_the_order_quote_only(int? l, int? w, int? h)
    {
        var quote = ShippingCalculator.Calculate([Zone()], District, [Line(SellerA, 1, 500, l, w, h)], 1);
        quote.Should().BeOfType<ShippingQuote.QuoteOnly>().Which.Reasons.Should().ContainSingle(r => r.Code == ShippingCalculator.Oversize);
    }

    [Fact]
    public void A_side_of_exactly_the_limit_and_unknown_sides_are_not_oversize()
    {
        Priced(Zone(), 1, Line(SellerA, 1, 500, 1_200, 1_200, 1_200)).TotalMinor.Should().Be(100);
        Priced(Zone(), 1, Line(SellerA, 1, 500)).TotalMinor.Should().Be(100);
    }

    [Fact]
    public void Quote_only_beats_free_shipping()
    {
        var quote = ShippingCalculator.Calculate([Zone()], District, [Line(SellerA, 1, 500, 2_000)], 9_999_999);
        quote.Should().BeOfType<ShippingQuote.QuoteOnly>();
    }

    [Fact]
    public void Destination_outside_every_zone_is_NoZone_and_inactive_zones_are_not_passed_in()
    {
        ShippingCalculator.Calculate([Zone()], OtherDistrict, [Line(SellerA, 1, 500)], 1).Should().BeOfType<ShippingQuote.NoZone>();
        ShippingCalculator.Calculate([], District, [Line(SellerA, 1, 500)], 1).Should().BeOfType<ShippingQuote.NoZone>();
    }

    [Fact]
    public void A_district_in_two_zones_is_refused()
    {
        var act = () => ShippingCalculator.Calculate([Zone(), Zone()], District, [Line(SellerA, 1, 500)], 1);
        act.Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void A_line_without_weight_is_an_error_not_free_shipping(int grams)
    {
        var act = () => ShippingCalculator.Calculate([Zone()], District, [Line(SellerA, 1, 500), Line(SellerB, 1, grams)], 1);
        act.Should().Throw<ValidationException>().Which.Errors.Single().Message.Should().Contain(ShippingCalculator.NoWeight);
    }

    [Fact]
    public void Bad_quantity_or_empty_order_is_an_error()
    {
        ((Action)(() => ShippingCalculator.Calculate([Zone()], District, [Line(SellerA, 0, 500)], 1))).Should().Throw<ValidationException>();
        ((Action)(() => ShippingCalculator.Calculate([Zone()], District, [], 1))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Each_seller_gets_its_own_parcel_and_fee_and_the_total_is_their_sum()
    {
        // A: 2 x 600 g = 1 200 g -> 250. B: 1 x 400 g -> 100. Pooled weight (1 600 g) would also be 250: the split must show.
        var quote = Priced(Zone(), 1, Line(SellerA, 2, 600), Line(SellerB, 1, 400));
        quote.PerSeller.Select(s => (s.SellerId, s.ShippingMinor, s.ChargeableGrams))
            .Should().Equal((SellerA, 250L, 1_200L), (SellerB, 100L, 400L));
        quote.TotalMinor.Should().Be(350);
    }

    [Fact]
    public void Two_lines_of_one_seller_are_one_parcel()
    {
        var quote = Priced(Zone(), 1, Line(SellerA, 1, 600), Line(SellerA, 1, 600));
        quote.PerSeller.Should().ContainSingle().Which.ShippingMinor.Should().Be(250);
    }

    [Fact]
    public void The_fee_cap_applies_even_if_a_card_were_inconsistent()
    {
        var quote = Priced(Zone(Card(max: 700)), 1, Line(SellerA, 1, 6_000));
        quote.TotalMinor.Should().BeLessThanOrEqualTo(700);
    }

    [Fact]
    public void Rate_card_rejects_inconsistent_tariffs()
    {
        var bad = new[]
        {
            () => new ShippingRateCard([], null, null, null, 100),
            () => new ShippingRateCard([new WeightBand(1000, 500), new WeightBand(2000, 400)], null, null, null, 1000), // heavier cheaper
            () => new ShippingRateCard([new WeightBand(1000, 500), new WeightBand(1000, 600)], null, null, null, 1000), // same upper weight
            () => new ShippingRateCard([new WeightBand(1000, 5000)], null, null, null, 1000),                           // above cap
            () => new ShippingRateCard([new WeightBand(0, 5)], null, null, null, 1000),
            () => new ShippingRateCard([new WeightBand(1000, -1)], null, null, null, 1000),
            () => new ShippingRateCard([new WeightBand(1000, 5)], 0, null, null, 1000),
        };
        foreach (var make in bad) ((Action)(() => make())).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Catalogue_units_convert_up_never_down()
    {
        ShippingLine.GramsFromKg(0.5m).Should().Be(500);
        ShippingLine.GramsFromKg(1.0001m).Should().Be(1_001);
        ShippingLine.GramsFromKg(1.000m).Should().Be(1_000);
        ShippingLine.MmFromCm(12.01m).Should().Be(121);
    }

    [Fact]
    public void Shipping_fees_feed_order_pricing_and_appear_in_the_grand_total_exactly_once()
    {
        var quote = Priced(Zone(), 1, Line(SellerA, 2, 600), Line(SellerB, 1, 400));
        var lines = new[]
        {
            new OrderLineDraft(Guid.NewGuid(), Guid.NewGuid(), SellerA, "A", "A", 1, 10_000, 0m, 0),
            new OrderLineDraft(Guid.NewGuid(), Guid.NewGuid(), SellerB, "B", "B", 1, 20_000, 0m, 0),
        };
        var terms = quote.PerSeller.Select(s => new SellerTerms(s.SellerId, s.ShippingMinor, 0.1m)).ToList();
        var priced = OrderPricing.Price(lines, terms, Mechware.Domain.Pricing.TaxMode.Exclusive);
        priced.ShippingMinor.Should().Be(quote.TotalMinor);
        priced.GrandTotalMinor.Should().Be(30_000 + quote.TotalMinor);
    }

    [Fact]
    public void Property_random_orders_hold_every_invariant()
    {
        var rng = new Random(20261113);
        var sellers = Enumerable.Range(0, 4).Select(_ => Guid.NewGuid()).ToArray();
        for (var round = 0; round < 500; round++)
        {
            // random monotone tariff
            var bandCount = rng.Next(1, 6);
            var bands = new List<WeightBand>();
            int grams = 0; long fee = rng.Next(0, 300);
            for (var i = 0; i < bandCount; i++)
            {
                grams += rng.Next(100, 6_000);
                fee += rng.Next(0, 400);
                bands.Add(new WeightBand(grams, fee));
            }

            var cap = fee + rng.Next(0, 500) + 1;
            long? free = rng.Next(3) == 0 ? null : rng.Next(1, 200_000);
            var card = new ShippingRateCard(bands, free, rng.Next(2) == 0 ? null : rng.Next(500, 25_000), null, cap);
            var zone = new ShippingZoneConfig(Guid.NewGuid(), "z", new HashSet<Guid> { District }, card);

            var lines = Enumerable.Range(0, rng.Next(1, 8))
                .Select(_ => new ShippingLine(Guid.NewGuid(), sellers[rng.Next(sellers.Length)], rng.Next(1, 6), rng.Next(1, 4_000)))
                .ToList();
            var subtotal = rng.Next(0, 300_000);

            var first = ShippingCalculator.Calculate([zone], District, lines, subtotal);
            var again = ShippingCalculator.Calculate([zone], District, lines, subtotal);
            System.Text.Json.JsonSerializer.Serialize(again, again.GetType()).Should().Be(System.Text.Json.JsonSerializer.Serialize(first, first.GetType())); // deterministic

            if (first is not ShippingQuote.Priced p)
            {
                // quote-only must be justified by a rule we can recompute
                var justified = lines.Any(l => card.HeavyUnitGrams is { } h && l.UnitGrams > h)
                    || lines.GroupBy(l => l.SellerId).Any(g => g.Sum(l => (long)l.Quantity * l.UnitGrams) > bands[^1].MaxGrams);
                justified.Should().BeTrue($"round {round}");
                continue;
            }

            p.PerSeller.Select(s => s.SellerId).Should().OnlyHaveUniqueItems();
            p.PerSeller.Should().OnlyContain(s => s.ShippingMinor >= 0 && s.ShippingMinor <= cap);
            p.TotalMinor.Should().Be(p.PerSeller.Sum(s => s.ShippingMinor), $"round {round}");
            var free_ = free is { } t && subtotal >= t;
            p.FreeShippingApplied.Should().Be(free_);
            if (free_) p.TotalMinor.Should().Be(0);

            // independent oracle per seller
            foreach (var s in p.PerSeller)
            {
                var w = lines.Where(l => l.SellerId == s.SellerId).Sum(l => (long)l.Quantity * l.UnitGrams);
                var expected = free_ ? 0 : Math.Min(bands.First(b => w <= b.MaxGrams).FeeMinor, cap);
                s.ShippingMinor.Should().Be(expected, $"round {round}");
            }

            // monotonic: one more unit of any line never makes that seller's fee smaller
            var bump = lines.Select((l, i) => i == 0 ? l with { Quantity = l.Quantity + 1 } : l).ToList();
            if (ShippingCalculator.Calculate([zone], District, bump, subtotal) is ShippingQuote.Priced q2)
            {
                var before = p.PerSeller.Single(s => s.SellerId == lines[0].SellerId).ShippingMinor;
                q2.PerSeller.Single(s => s.SellerId == lines[0].SellerId).ShippingMinor.Should().BeGreaterThanOrEqualTo(before);
                q2.TotalMinor.Should().BeGreaterThanOrEqualTo(p.TotalMinor);
            }
        }
    }
}
