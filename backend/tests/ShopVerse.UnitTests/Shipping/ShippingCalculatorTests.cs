using FluentAssertions;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Shipping;

namespace ShopVerse.UnitTests.Shipping;

public class ShippingCalculatorTests
{
    // 60.00 base, 20.00 per kg, steps of 0.5 kg, volumetric divisor 5000.
    private static readonly ShippingRateCard Dhaka = new(ShippingZone.InsideDhaka, 6_000, 2_000);

    private static ShippingItem Item(int qty, int grams, int l = 100, int w = 100, int h = 100) => new(qty, grams, l, w, h);

    [Fact]
    public void BasePlusPerKilo_OnTheRealWeight()
    {
        // 2 kg real, small box (100 mm cube = 1000 cm³ -> 0.2 kg volumetric): chargeable 2.0 kg -> 60.00 + 2 x 20.00.
        var q = ShippingCalculator.Quote(Dhaka, [Item(1, 2_000)]);
        (q.AmountMinor, q.ChargeableGrams, q.QuoteRequired).Should().Be((10_000, 2_000, false));
    }

    [Fact]
    public void VolumetricWeight_WinsWhenTheBoxIsLightButBig()
    {
        // 500 x 400 x 300 mm = 60,000 cm³ / 5000 = 12 kg volumetric, real weight only 1 kg.
        var q = ShippingCalculator.Quote(Dhaka, [Item(1, 1_000, 500, 400, 300)]);
        q.ChargeableGrams.Should().Be(12_000);
        q.AmountMinor.Should().Be(6_000 + 12 * 2_000);
    }

    [Theory]
    [InlineData(1, 500)]       // exactly one step
    [InlineData(501, 1_000)]   // one gram over rounds up to the next step
    [InlineData(1_000, 1_000)]
    [InlineData(1_001, 1_500)]
    public void TheChargeableWeightRoundsUpToTheStep(int grams, int expectedChargeable)
    {
        ShippingCalculator.Quote(Dhaka, [Item(1, grams)]).ChargeableGrams.Should().Be(expectedChargeable);
    }

    [Fact]
    public void AWeightlessItemStillOccupiesItsBox_AndAnEmptySizeAndWeightCostsOnlyTheBase()
    {
        // 0 g in a 100 mm cube still has 200 g of volumetric weight, so it is one step.
        ShippingCalculator.Quote(Dhaka, [Item(1, 0)]).ChargeableGrams.Should().Be(500);
        // Nothing at all to ship (no weight, no size) has no chargeable weight: just the base.
        var q = ShippingCalculator.Quote(Dhaka, [new ShippingItem(1, 0, 0, 0, 0)]);
        (q.ChargeableGrams, q.AmountMinor).Should().Be((0, 6_000));
    }

    [Fact]
    public void QuantityMultipliesEachItemsOwnChargeableWeight()
    {
        var q = ShippingCalculator.Quote(Dhaka, [Item(3, 700), Item(2, 100, 400, 400, 400)]);
        // item 1: 3 x 700 = 2,100 g; item 2: 400³ mm = 64,000 cm³ / 5000 = 12.8 kg -> 12,800 g each, 2 of them = 25,600 g.
        q.ChargeableGrams.Should().Be(28_000);   // 27,700 g rounded up to the next 500
    }

    [Fact]
    public void ZeroPerKilo_IsAFlatFee()
    {
        var flat = Dhaka with { PerKgMinor = 0 };
        ShippingCalculator.Quote(flat, [Item(1, 9_000)]).AmountMinor.Should().Be(6_000);
    }

    [Fact]
    public void HeavyParcels_AreQuoteOnly_NotGuessed()
    {
        var limited = Dhaka with { MaxParcelGrams = 30_000 };
        ShippingCalculator.Quote(limited, [Item(1, 30_000)]).QuoteRequired.Should().BeFalse();
        var q = ShippingCalculator.Quote(limited, [Item(1, 30_001)]);
        (q.QuoteRequired, q.Reason, q.AmountMinor).Should().Be((true, ShippingCalculator.TooHeavy, 0));
    }

    [Fact]
    public void OversizeItems_AreQuoteOnly_EvenIfLight()
    {
        var limited = Dhaka with { MaxDimensionMm = 1_200 };
        ShippingCalculator.Quote(limited, [Item(1, 100, 1_200, 100, 100)]).QuoteRequired.Should().BeFalse();
        var q = ShippingCalculator.Quote(limited, [Item(1, 100, 100, 1_201, 100)]);
        (q.QuoteRequired, q.Reason).Should().Be((true, ShippingCalculator.Oversize));
    }

    [Fact]
    public void ABadTariffOrShipment_IsRefused()
    {
        ((Action)(() => ShippingCalculator.Quote(Dhaka with { BaseMinor = -1 }, [Item(1, 1)]))).Should().Throw<ValidationException>();
        ((Action)(() => ShippingCalculator.Quote(Dhaka with { VolumetricDivisor = 0 }, [Item(1, 1)]))).Should().Throw<ValidationException>();
        ((Action)(() => ShippingCalculator.Quote(Dhaka with { WeightStepGrams = 0 }, [Item(1, 1)]))).Should().Throw<ValidationException>();
        ((Action)(() => ShippingCalculator.Quote(Dhaka, []))).Should().Throw<ValidationException>();
        ((Action)(() => ShippingCalculator.Quote(Dhaka, [Item(0, 1)]))).Should().Throw<ValidationException>();
        ((Action)(() => ShippingCalculator.Quote(Dhaka, [Item(1, -1)]))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void TheFeeNeverDecreasesAsTheParcelGetsHeavier()
    {
        long previous = -1;
        for (var grams = 0; grams <= 40_000; grams += 137)
        {
            var amount = ShippingCalculator.Quote(Dhaka, [Item(1, grams)]).AmountMinor;
            amount.Should().BeGreaterThanOrEqualTo(previous, $"at {grams} g");
            previous = amount;
        }
    }

    [Fact]
    public void MoreOfTheSameItemNeverCostsLess()
    {
        long previous = -1;
        for (var qty = 1; qty <= 50; qty++)
        {
            var amount = ShippingCalculator.Quote(Dhaka, [Item(qty, 650, 300, 200, 100)]).AmountMinor;
            amount.Should().BeGreaterThanOrEqualTo(previous);
            previous = amount;
        }
    }
}
