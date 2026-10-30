using FluentAssertions;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Pricing;

namespace ShopVerse.UnitTests.Catalog;

public class PricingEngineTests
{
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private static Money Tk(decimal v) => new(v, Currency.BDT);

    private static PricingInput Input(decimal price, decimal? compareAt = null, decimal? sale = null,
        DateTime? from = null, DateTime? to = null, decimal vat = 0.05m) =>
        new(Tk(price), compareAt is null ? null : Tk(compareAt.Value), sale is null ? null : Tk(sale.Value), from, to, vat);

    [Fact]
    public void RegularPrice_NoCompareAt_ShowsNoStrikeThroughOrDiscount()
    {
        var q = PricingEngine.Quote(Input(1000m), T0, TaxMode.Inclusive);

        q.Display.Should().Be(Tk(1000m));
        q.StrikeThrough.Should().BeNull();
        q.DiscountPercent.Should().Be(0);
        q.SaleActive.Should().BeFalse();
    }

    [Theory]
    [InlineData(1000, 1300, 23)]   // 23.07% → 23
    [InlineData(1000, 1250, 20)]   // exactly 20%
    [InlineData(999, 1000, 0)]     // 0.1% rounds DOWN to 0
    [InlineData(7650, 10000, 23)]  // 23.5% rounds DOWN to 23 (not 24)
    [InlineData(1, 3, 66)]         // 66.67% → 66
    public void DiscountPercent_RoundsDownToWholePercent(decimal price, decimal compareAt, int expected)
    {
        PricingEngine.DiscountPercent(Tk(compareAt), Tk(price)).Should().Be(expected);
        PricingEngine.Quote(Input(price, compareAt), T0, TaxMode.Inclusive).DiscountPercent.Should().Be(expected);
    }

    [Fact]
    public void CompareAtNotAbovePrice_IsIgnored()
    {
        var q = PricingEngine.Quote(Input(1000m, compareAt: 1000m), T0, TaxMode.Inclusive);
        q.StrikeThrough.Should().BeNull();
        q.DiscountPercent.Should().Be(0);

        PricingEngine.Quote(Input(1000m, compareAt: 900m), T0, TaxMode.Inclusive).StrikeThrough.Should().BeNull();
    }

    [Fact]
    public void SaleWindow_StartIsInclusive_EndIsExclusive()
    {
        var input = Input(1000m, sale: 800m, from: T0, to: T0.AddDays(2));

        PricingEngine.Quote(input, T0.AddTicks(-1), TaxMode.Inclusive).SaleActive.Should().BeFalse();
        PricingEngine.Quote(input, T0, TaxMode.Inclusive).SaleActive.Should().BeTrue();
        PricingEngine.Quote(input, T0.AddDays(2).AddTicks(-1), TaxMode.Inclusive).SaleActive.Should().BeTrue();
        PricingEngine.Quote(input, T0.AddDays(2), TaxMode.Inclusive).SaleActive.Should().BeFalse();
    }

    [Fact]
    public void ActiveSale_OverridesPrice_AndStrikesThroughRegularPrice()
    {
        var q = PricingEngine.Quote(Input(1000m, sale: 750m, from: T0, to: T0.AddDays(1)), T0.AddHours(1), TaxMode.Inclusive);

        q.Display.Should().Be(Tk(750m));
        q.StrikeThrough.Should().Be(Tk(1000m));
        q.DiscountPercent.Should().Be(25);
        q.SaleActive.Should().BeTrue();
    }

    [Fact]
    public void ActiveSale_PrefersHigherCompareAtAsStrikeThrough()
    {
        var q = PricingEngine.Quote(Input(1000m, compareAt: 1500m, sale: 750m, from: T0, to: T0.AddDays(1)), T0, TaxMode.Inclusive);

        q.StrikeThrough.Should().Be(Tk(1500m));
        q.DiscountPercent.Should().Be(50);
    }

    [Fact]
    public void ExpiredSale_FallsBackToRegularPrice()
    {
        var q = PricingEngine.Quote(Input(1000m, sale: 750m, from: T0, to: T0.AddDays(1)), T0.AddDays(3), TaxMode.Inclusive);

        q.Display.Should().Be(Tk(1000m));
        q.SaleActive.Should().BeFalse();
        q.DiscountPercent.Should().Be(0);
    }

    [Fact]
    public void SalePriceWithoutWindow_IsNeverActive()
    {
        PricingEngine.Quote(Input(1000m, sale: 750m), T0, TaxMode.Inclusive).SaleActive.Should().BeFalse();
    }

    [Fact]
    public void InclusiveVat_ExtractsTaxFromGrossPrice()
    {
        // 1,050.00 gross @ 5% → net 1,000.00, tax 50.00
        var q = PricingEngine.Quote(Input(1050m), T0, TaxMode.Inclusive);

        q.Gross.Should().Be(Tk(1050m));
        q.Net.Should().Be(Tk(1000m));
        q.Tax.Should().Be(Tk(50m));
        q.Display.Should().Be(Tk(1050m));
    }

    [Fact]
    public void ExclusiveVat_AddsTaxOnTopOfNetPrice()
    {
        var q = PricingEngine.Quote(Input(1000m), T0, TaxMode.Exclusive);

        q.Net.Should().Be(Tk(1000m));
        q.Tax.Should().Be(Tk(50m));
        q.Gross.Should().Be(Tk(1050m));
        q.Display.Should().Be(Tk(1000m), "international storefronts headline the tax-exclusive price");
    }

    [Theory]
    [InlineData(0.05, 12345.67)]
    [InlineData(0.15, 999.99)]
    [InlineData(0.075, 1.01)]
    [InlineData(0.0, 500.00)]
    public void Net_Plus_Tax_AlwaysEqualsGross_NoRoundingDrift(double vat, double price)
    {
        foreach (var mode in new[] { TaxMode.Inclusive, TaxMode.Exclusive })
        {
            var q = PricingEngine.Quote(Input((decimal)price, vat: (decimal)vat), T0, mode);
            (q.Net + q.Tax).Should().Be(q.Gross, $"{mode} @ {vat} on {price}");
        }
    }

    [Fact]
    public void VatRounding_UsesBankersRounding()
    {
        // 0.025 * 5% style midpoint: price 10.50 excl. @ 5% = 0.525 → ToEven → 0.52
        var q = PricingEngine.Quote(Input(10.50m), T0, TaxMode.Exclusive);
        q.Tax.Should().Be(Tk(0.52m));
    }

    [Fact]
    public void LargeAmounts_KeepFull128BitDecimalPrecision()
    {
        var q = PricingEngine.Quote(Input(79_228_162_514m), T0, TaxMode.Exclusive);
        (q.Net + q.Tax).Should().Be(q.Gross);
        q.Tax.Amount.Should().Be(3_961_408_125.70m);
    }

    [Fact]
    public void InvalidVat_Throws()
    {
        var act = () => PricingEngine.Quote(Input(100m, vat: 0.6m), T0, TaxMode.Inclusive);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void MixedCurrencies_Throw()
    {
        var input = new PricingInput(Tk(100m), new Money(150m, Currency.USD), null, null, null, 0.05m);
        var act = () => PricingEngine.Quote(input, T0, TaxMode.Inclusive);
        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Quote_IsDeterministic()
    {
        var input = Input(1234.5m, 1500m, 999m, T0, T0.AddDays(1));
        PricingEngine.Quote(input, T0.AddHours(3), TaxMode.Inclusive)
            .Should().Be(PricingEngine.Quote(input, T0.AddHours(3), TaxMode.Inclusive));
    }
}
