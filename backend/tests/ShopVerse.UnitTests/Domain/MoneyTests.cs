using FluentAssertions;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;
using Xunit;

namespace ShopVerse.UnitTests.Domain;

public class MoneyTests
{
    [Fact]
    public void Constructor_ShouldRoundUsingBankersRounding_ToEven()
    {
        // 2.505 -> 2.50 (0 is even)
        new Money(2.505m, Currency.BDT).Amount.Should().Be(2.50m);

        // 2.515 -> 2.52 (2 is even)
        new Money(2.515m, Currency.BDT).Amount.Should().Be(2.52m);

        // 2.525 -> 2.52 (2 is even)
        new Money(2.525m, Currency.BDT).Amount.Should().Be(2.52m);

        // 2.535 -> 2.54 (4 is even)
        new Money(2.535m, Currency.BDT).Amount.Should().Be(2.54m);

        // 2.545 -> 2.54 (4 is even)
        new Money(2.545m, Currency.BDT).Amount.Should().Be(2.54m);

        // 2.555 -> 2.56 (6 is even)
        new Money(2.555m, Currency.BDT).Amount.Should().Be(2.56m);
    }

    [Fact]
    public void FromMinorUnits_ShouldConvertAccurately()
    {
        var money = Money.FromMinorUnits(125050L, Currency.BDT);
        money.Amount.Should().Be(1250.50m);
        money.Currency.Should().Be(Currency.BDT);

        var usd = Money.FromMinorUnits(5000L, Currency.USD);
        usd.Amount.Should().Be(50.00m);
        usd.Currency.Should().Be(Currency.USD);
    }

    [Fact]
    public void ToMinorUnits_ShouldConvertAccurately()
    {
        var money = new Money(1250.50m, Currency.BDT);
        money.ToMinorUnits().Should().Be(125050L);

        var negative = new Money(-45.25m, Currency.USD);
        negative.ToMinorUnits().Should().Be(-4525L);
    }

    [Fact]
    public void Addition_WithSameCurrency_ShouldSucceed()
    {
        var m1 = new Money(100.25m, Currency.BDT);
        var m2 = new Money(50.75m, Currency.BDT);

        var result = m1 + m2;

        result.Amount.Should().Be(151.00m);
        result.Currency.Should().Be(Currency.BDT);
    }

    [Fact]
    public void Addition_WithDifferentCurrency_ShouldThrowCurrencyMismatchException()
    {
        var m1 = new Money(100.00m, Currency.BDT);
        var m2 = new Money(100.00m, Currency.USD);

        var act = () => _ = m1 + m2;

        act.Should().Throw<CurrencyMismatchException>()
            .WithMessage("*Cannot perform monetary operations between BDT and USD*");
    }

    [Fact]
    public void Subtraction_WithSameCurrency_ShouldSucceed()
    {
        var m1 = new Money(100.00m, Currency.BDT);
        var m2 = new Money(35.50m, Currency.BDT);

        var result = m1 - m2;

        result.Amount.Should().Be(64.50m);
    }

    [Fact]
    public void Subtraction_WithDifferentCurrency_ShouldThrow()
    {
        var m1 = new Money(100m, Currency.BDT);
        var m2 = new Money(100m, Currency.EUR);

        var act = () => _ = m1 - m2;

        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Negation_ShouldInvertSign()
    {
        var positive = new Money(50.00m, Currency.BDT);
        var negative = -positive;

        negative.Amount.Should().Be(-50.00m);
        (-negative).Amount.Should().Be(50.00m);
    }

    [Fact]
    public void Multiplication_ByScalar_ShouldApplyBankersRounding()
    {
        var money = new Money(10.00m, Currency.BDT);

        // 10 * 1.0555 = 10.555 -> rounds to even 10.56
        var result1 = money * 1.0555m;
        result1.Amount.Should().Be(10.56m);

        // 10 * 1.0545 = 10.545 -> rounds to even 10.54
        var result2 = money * 1.0545m;
        result2.Amount.Should().Be(10.54m);

        var result3 = 2.5m * money;
        result3.Amount.Should().Be(25.00m);

        var intResult = money * 3;
        intResult.Amount.Should().Be(30.00m);
    }

    [Fact]
    public void Division_ByScalar_ShouldApplyBankersRounding()
    {
        var money = new Money(100.00m, Currency.BDT);

        var divided = money / 3m;
        // 33.33333... -> 33.33
        divided.Amount.Should().Be(33.33m);

        var dividedInt = money / 4;
        dividedInt.Amount.Should().Be(25.00m);
    }

    [Fact]
    public void Division_ByZero_ShouldThrowDivideByZeroException()
    {
        var money = new Money(100.00m, Currency.BDT);

        var act1 = () => _ = money / 0m;
        act1.Should().Throw<DivideByZeroException>();

        var act2 = () => _ = money / 0;
        act2.Should().Throw<DivideByZeroException>();
    }

    [Fact]
    public void Comparisons_ShouldWorkCorrectly()
    {
        var m1 = new Money(10.00m, Currency.BDT);
        var m2 = new Money(20.00m, Currency.BDT);
        var m3 = new Money(10.00m, Currency.BDT);

        (m1 < m2).Should().BeTrue();
        (m2 > m1).Should().BeTrue();
        (m1 <= m3).Should().BeTrue();
        (m1 >= m3).Should().BeTrue();
        (m1 == m3).Should().BeTrue();
        (m1 != m2).Should().BeTrue();
        m1.CompareTo(m2).Should().BeNegative();
        m2.CompareTo(m1).Should().BePositive();
        m1.CompareTo(m3).Should().Be(0);
    }

    [Fact]
    public void Comparison_BetweenDifferentCurrencies_ShouldThrow()
    {
        var m1 = new Money(10.00m, Currency.BDT);
        var m2 = new Money(10.00m, Currency.USD);

        var act = () => _ = m1 < m2;

        act.Should().Throw<CurrencyMismatchException>();
    }

    [Fact]
    public void Distribute_ProportionalSplit_ShouldNotLoseMinorUnits()
    {
        var money = new Money(100.00m, Currency.BDT); // 10,000 minor units

        // Split into 3 parts: 10000 / 3 = 3333 with remainder 1
        // Parts: 3334, 3333, 3333
        var shares = money.Distribute(3);

        shares.Should().HaveCount(3);
        shares[0].Amount.Should().Be(33.34m);
        shares[1].Amount.Should().Be(33.33m);
        shares[2].Amount.Should().Be(33.33m);

        // Sum of parts must equal original amount down to the last poisha
        var sum = shares[0] + shares[1] + shares[2];
        sum.Should().Be(money);
    }

    [Fact]
    public void Distribute_WithZeroOrNegativeParts_ShouldThrow()
    {
        var money = new Money(50m, Currency.BDT);

        var act0 = () => money.Distribute(0);
        act0.Should().Throw<ArgumentOutOfRangeException>();

        var actNeg = () => money.Distribute(-2);
        actNeg.Should().Throw<ArgumentOutOfRangeException>();
    }

    [Fact]
    public void StateFlags_ShouldReflectValue()
    {
        Money.Zero(Currency.BDT).IsZero.Should().BeTrue();
        new Money(10m, Currency.BDT).IsPositive.Should().BeTrue();
        new Money(-10m, Currency.BDT).IsNegative.Should().BeTrue();
    }

    [Fact]
    public void Formatting_ShouldContainSymbolAndDecimalFormat()
    {
        var bdt = new Money(1250.50m, Currency.BDT);
        bdt.ToString().Should().Contain("৳");

        var usd = new Money(50.25m, Currency.USD);
        usd.ToString().Should().Contain("$");
        usd.ToIsoFormattedString().Should().Be("50.25 USD");
    }
}
