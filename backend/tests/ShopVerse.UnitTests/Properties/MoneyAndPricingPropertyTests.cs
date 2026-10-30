using FluentAssertions;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Pricing;
using static ShopVerse.UnitTests.Properties.PropertyRunner;

namespace ShopVerse.UnitTests.Properties;

/// <summary>Randomised invariants for money arithmetic and the pricing engine (brief §8.1, ADR-0002).</summary>
public class MoneyAndPricingPropertyTests
{
    private static readonly Currency[] Currencies = [Currency.BDT, Currency.USD, Currency.EUR];
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void MinorUnits_RoundTripExactly()
    {
        ForAll(101, DefaultIterations, (rng, _) =>
        {
            var minor = rng.NextInt64(-1_000_000_000_000_00L, 1_000_000_000_000_00L);
            var currency = Currencies[rng.Next(Currencies.Length)];
            Money.FromMinorUnits(minor, currency).ToMinorUnits().Should().Be(minor);
        });
    }

    [Fact]
    public void Construction_AlwaysYieldsTwoDecimals_UsingBankersRounding()
    {
        ForAll(102, DefaultIterations, (rng, _) =>
        {
            var raw = rng.RawDecimal();
            var m = new Money(raw);
            (m.Amount * 100m % 1m).Should().Be(0m, "amount {0} must have at most 2 decimals", m.Amount);
            m.Amount.Should().Be(decimal.Round(raw, 2, MidpointRounding.ToEven));
            Math.Abs(m.Amount - raw).Should().BeLessThanOrEqualTo(0.005m);
        });
    }

    [Fact]
    public void Addition_IsCommutativeAssociative_AndSubtractionInvertsIt()
    {
        ForAll(103, DefaultIterations, (rng, _) =>
        {
            var a = new Money(rng.Amount());
            var b = new Money(rng.Amount());
            var c = new Money(rng.Amount());
            (a + b).Should().Be(b + a);
            ((a + b) + c).Should().Be(a + (b + c));
            ((a + b) - b).Should().Be(a);
            (a - a).IsZero.Should().BeTrue();
            (-(-a)).Should().Be(a);
        });
    }

    [Fact]
    public void IntegerMultiplication_EqualsRepeatedAddition()
    {
        ForAll(104, 1_000, (rng, _) =>
        {
            var a = new Money(rng.Amount(1_000_000_00L));
            var n = rng.Next(0, 60);
            var sum = Money.Zero();
            for (var i = 0; i < n; i++) sum += a;
            (a * n).Should().Be(sum);
        });
    }

    [Fact]
    public void Distribute_NeverLosesOrCreatesAPoisha_AndSharesDifferByAtMostOne()
    {
        ForAll(105, DefaultIterations, (rng, _) =>
        {
            var total = new Money(rng.Amount(1_000_000_000_00L, allowNegative: false));
            var parts = rng.Next(1, 97);
            var shares = total.Distribute(parts);

            shares.Should().HaveCount(parts);
            shares.Sum(s => s.ToMinorUnits()).Should().Be(total.ToMinorUnits());
            var minors = shares.Select(s => s.ToMinorUnits()).ToList();
            (minors.Max() - minors.Min()).Should().BeLessThanOrEqualTo(1);
            minors.Should().BeInDescendingOrder("the remainder goes to the first shares, deterministically");
        });
    }

    [Fact]
    public void MixedCurrencyArithmetic_IsAlwaysRejected()
    {
        ForAll(106, 500, (rng, _) =>
        {
            var a = new Money(rng.Amount(), Currency.BDT);
            var b = new Money(rng.Amount(), rng.Next(2) == 0 ? Currency.USD : Currency.EUR);
            FluentActions.Invoking(() => a + b).Should().Throw<CurrencyMismatchException>();
            FluentActions.Invoking(() => a - b).Should().Throw<CurrencyMismatchException>();
        });
    }

    private static PricingInput RandomInput(Random rng, out DateTime asOf)
    {
        var price = rng.Amount(10_000_000_00L, allowNegative: false) + 0.01m;
        Money? compare = rng.Next(3) == 0 ? null : new Money(rng.Amount(20_000_000_00L, allowNegative: false));
        Money? sale = rng.Next(3) == 0 ? null : new Money(rng.Amount(12_000_000_00L, allowNegative: false));
        DateTime? start = rng.Next(4) == 0 ? null : T0.AddHours(rng.Next(-500, 500));
        DateTime? end = start is null || rng.Next(5) == 0 ? null : start.Value.AddHours(rng.Next(-10, 400));
        asOf = T0.AddHours(rng.Next(-600, 600)).AddSeconds(rng.Next(0, 3600));
        var vat = rng.Next(0, 51) / 100m;
        return new PricingInput(new Money(price), compare, sale, start, end, vat);
    }

    [Fact]
    public void Quote_InvariantsHoldForEveryInput()
    {
        ForAll(201, 20_000, (rng, _) =>
        {
            var input = RandomInput(rng, out var asOf);
            var mode = rng.Next(2) == 0 ? TaxMode.Inclusive : TaxMode.Exclusive;
            var q = PricingEngine.Quote(input, asOf, mode);

            // Money is conserved: net + tax = gross, to the poisha.
            (q.Net + q.Tax).Should().Be(q.Gross);
            q.Tax.IsNegative.Should().BeFalse();
            q.Net.Should().BeLessThanOrEqualTo(q.Gross);

            // A sale can only ever lower what the buyer pays.
            var effective = mode == TaxMode.Inclusive ? q.Gross : q.Net;
            effective.Should().BeLessThanOrEqualTo(input.Price);
            q.Display.Should().Be(effective);

            // Sale window is half-open [start, end) and requires a lower, positive sale price.
            var expectedActive = input.SalePrice is { } s && s.IsPositive && s < input.Price
                && input.SaleStartsAtUtc is { } st && input.SaleEndsAtUtc is { } en && asOf >= st && asOf < en;
            q.SaleActive.Should().Be(expectedActive);
            effective.Should().Be(expectedActive ? input.SalePrice!.Value : input.Price);

            // Strike-through is always above the price paid; discount is a floor percentage in [1, 99].
            if (q.StrikeThrough is { } strike)
            {
                strike.Should().BeGreaterThan(effective);
                q.DiscountPercent.Should().BeInRange(0, 99);
                var exact = (strike.Amount - effective.Amount) / strike.Amount * 100m;
                q.DiscountPercent.Should().Be((int)decimal.Floor(exact));
            }
            else
            {
                q.DiscountPercent.Should().Be(0);
            }

            // Exclusive VAT is computed from net with banker's rounding.
            if (mode == TaxMode.Exclusive)
                q.Tax.Should().Be(new Money(q.Net.Amount * input.VatRate));

            // Pure function: same input, same output.
            PricingEngine.Quote(input, asOf, mode).Should().Be(q);
        });
    }

    [Fact]
    public void InclusiveTax_ExtractedVatNeverDriftsMoreThanHalfAPoisha()
    {
        ForAll(202, 20_000, (rng, _) =>
        {
            var gross = new Money(rng.Amount(10_000_000_00L, allowNegative: false) + 0.01m);
            var vat = rng.Next(0, 51) / 100m;
            var q = PricingEngine.Quote(new PricingInput(gross, null, null, null, null, vat), T0, TaxMode.Inclusive);
            var exactNet = gross.Amount / (1m + vat);
            Math.Abs(q.Net.Amount - exactNet).Should().BeLessThanOrEqualTo(0.005m);
        });
    }

    [Fact]
    public void VatRateOutsideRange_IsRejected()
    {
        ForAll(203, 500, (rng, _) =>
        {
            var bad = rng.Next(2) == 0 ? -(rng.Next(1, 1000) / 1000m) : 0.5m + rng.Next(1, 1000) / 1000m;
            var input = new PricingInput(new Money(100m), null, null, null, null, bad);
            FluentActions.Invoking(() => PricingEngine.Quote(input, T0, TaxMode.Inclusive)).Should().Throw<ValidationException>();
        });
    }
}
