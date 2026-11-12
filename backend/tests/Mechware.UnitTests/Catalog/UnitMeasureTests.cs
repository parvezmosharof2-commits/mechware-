using FluentAssertions;
using Mechware.Domain.Catalog;

namespace Mechware.UnitTests.Catalog;

public class UnitMeasureTests
{
    [Theory]
    [InlineData(0.75, "kW", "power", 750)]
    [InlineData(10, "bar", "pressure", 1_000_000)]
    [InlineData(2, "kPa", "pressure", 2_000)]
    [InlineData(500, "mA", "current", 0.5)]
    [InlineData(1, "HP", "power", 745.7)]
    [InlineData(91.4, "%", "ratio", 91.4)]
    public void TryToBase_ConvertsToTheBaseUnit(double value, string unit, string quantity, double expected)
    {
        UnitCatalog.TryToBase((decimal)value, unit, out var q, out var b).Should().BeTrue();
        q.Should().Be(quantity);
        ((double)b).Should().BeApproximately(expected, 0.0001);
    }

    [Fact]
    public void TryToBase_UnknownUnit_IsFalse() => UnitCatalog.TryToBase(1, "furlong", out _, out _).Should().BeFalse();

    [Theory]
    [InlineData("750 W", "power", 750, "")]
    [InlineData("0.75kW", "power", 750, "")]
    [InlineData("siemens 11 kW inverter", "power", 11000, "siemens inverter")]
    [InlineData("  400 v   motor ", "voltage", 400, "motor")]
    [InlineData("3 phase 24 V", "voltage", 24, "3 phase")]
    public void TryReadMeasure_FindsTheFirstSingleMeasure(string text, string quantity, double value, string remainder)
    {
        UnitCatalog.TryReadMeasure(text, out var q, out var b, out var rest).Should().BeTrue();
        q.Should().Be(quantity);
        ((double)b).Should().BeApproximately(value, 0.0001);
        rest.Should().Be(remainder);
    }

    [Theory]
    [InlineData("0-10 bar")]
    [InlineData("siemens inverter")]
    [InlineData("6ES7214-1AG40-0XB0")]
    [InlineData("1 kWh")]
    [InlineData("")]
    [InlineData(null)]
    public void TryReadMeasure_NoSingleMeasure_IsFalse(string? text)
    {
        UnitCatalog.TryReadMeasure(text, out _, out _, out var rest).Should().BeFalse();
        rest.Should().Be(text ?? "");
    }

    [Fact]
    public void Quantities_ListTheKnownOnes() =>
        UnitCatalog.Quantities.Should().Contain(["power", "voltage", "current", "length", "pressure", "frequency", "ratio"]);
}
