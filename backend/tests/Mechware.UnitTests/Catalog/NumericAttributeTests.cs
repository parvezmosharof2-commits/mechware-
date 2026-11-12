using FluentAssertions;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.UnitTests.Catalog;

/// <summary>
/// PIM (sprint 3–4): technical attributes with units. A numeric value is read from what a seller writes ("0.75 kW (1 HP)",
/// "750 W", "0–10 bar") and stored in the attribute's unit, so values written in different units compare and filter.
/// </summary>
public class NumericAttributeTests
{
    private static AttributeDefinition Attr(AttributeDataType type, string? unit) =>
        new(Guid.NewGuid(), "Rated power", "রেটেড পাওয়ার", "rated_power", type, unit, true);

    [Theory]
    [InlineData("0.75 kW (1 HP)", "kW", 0.75)]
    [InlineData("750 W", "kW", 0.75)]
    [InlineData("1 HP", "kW", 0.7457)]
    [InlineData("11 kW @ 400 V", "kW", 11)]
    [InlineData("2 kW", "W", 2000)]
    [InlineData("Ø32 mm", "mm", 32)]
    [InlineData("0.1 m", "mm", 100)]
    [InlineData("10 bar", "MPa", 1)]
    [InlineData("145 psi", "bar", 9.9974)]
    [InlineData("12 A", "A", 12)]
    [InlineData("500 mA", "A", 0.5)]
    [InlineData("6 kA", "A", 6000)]
    [InlineData("10 KA", "kA", 10)]
    [InlineData("91.4 %", "%", 91.4)]
    [InlineData("50 Hz", "Hz", 50)]
    [InlineData("0.75 KW", "kW", 0.75)]
    [InlineData("3 phases, 400 V", "V", 400)]   // "phases" is not a unit: read on to the value
    public void ASingleValue_IsReadInTheAttributesUnit(string text, string unit, double expected)
    {
        var (min, max) = UnitCatalog.Parse(text, unit, allowRange: false);
        ((double)min).Should().BeApproximately(expected, 0.0001);
        max.Should().Be(min);
    }

    [Theory]
    [InlineData("0–10 bar", "bar", 0, 10)]
    [InlineData("85–264 V AC", "V", 85, 264)]
    [InlineData("1-phase 200–240 V", "V", 200, 240)]
    [InlineData("24 V DC", "V", 24, 24)]
    [InlineData("0-1 MPa", "bar", 0, 10)]
    public void ARange_KeepsBothEnds(string text, string unit, double low, double high)
    {
        var (min, max) = UnitCatalog.Parse(text, unit, allowRange: true);
        ((double)min).Should().BeApproximately(low, 0.001);
        ((double)max).Should().BeApproximately(high, 0.001);
    }

    [Theory]
    [InlineData("12 A", "kW")]          // a current is not a power
    [InlineData("0.75 kW", "mm")]
    [InlineData("3 furlongs", "mm")]    // unknown unit
    [InlineData("M12", "mm")]           // no number with a unit
    [InlineData("", "kW")]
    [InlineData("   ", "kW")]
    [InlineData("kW", "kW")]
    [InlineData("10–2 bar", "bar")]     // a range must not run backwards
    [InlineData("99999999999 kW", "kW")] // beyond any real part
    [InlineData("1000000000000000 W", "kW")] // its last digits alone must not read as "0 W"
    [InlineData("4 mm²", "mm")]          // an area, not a length
    [InlineData("3 kWh", "kW")]          // an energy, not a power
    public void TextThatIsNotAValueOfTheUnit_IsRefused(string text, string unit)
    {
        var act = () => UnitCatalog.Parse(text, unit, allowRange: true);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void ARange_IsRefused_WhereOneValueIsExpected()
    {
        var act = () => UnitCatalog.Parse("0–10 bar", "bar", allowRange: false);
        act.Should().Throw<ValidationException>().WithMessage("*single*");
    }

    [Fact]
    public void ANumericAttribute_StoresTheNumber_WithTheTextTheSellerWrote()
    {
        var power = Attr(AttributeDataType.Number, "kW");
        var kw = power.AddValue(Guid.NewGuid(), "0.75 kW (1 HP)", "0.75 kW (1 HP)", 0);
        var w = power.AddValue(Guid.NewGuid(), "750 W", "750 W", 1);

        kw.ValueEn.Should().Be("0.75 kW (1 HP)");
        kw.NumericValue.Should().Be(0.75m);
        w.NumericValue.Should().Be(0.75m, "values in different units compare once read in the attribute's unit");
        kw.NumericMax.Should().Be(0.75m);
    }

    [Fact]
    public void ARangeAttribute_StoresBothEnds()
    {
        var pressure = new AttributeDefinition(Guid.NewGuid(), "Pressure range", "চাপের সীমা", "pressure_range", AttributeDataType.Range, "bar", true);
        var v = pressure.AddValue(Guid.NewGuid(), "0–16 bar", "0–16 bar", 0);
        (v.NumericValue, v.NumericMax).Should().Be((0m, 16m));
    }

    [Fact]
    public void ATextAttribute_KeepsNoNumber()
    {
        var thread = new AttributeDefinition(Guid.NewGuid(), "Body thread", "থ্রেড", "body_thread", AttributeDataType.String, null, true);
        var v = thread.AddValue(Guid.NewGuid(), "M12", "M12", 0);
        v.NumericValue.Should().BeNull();
        v.NumericMax.Should().BeNull();
    }

    [Fact]
    public void ANumericValue_ThatCannotBeRead_IsRefused()
    {
        var act = () => Attr(AttributeDataType.Number, "kW").AddValue(Guid.NewGuid(), "12 A", "12 A", 0);
        act.Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData(AttributeDataType.Number, null)]
    [InlineData(AttributeDataType.Number, "furlong")]
    [InlineData(AttributeDataType.Range, null)]
    [InlineData(AttributeDataType.String, "kW")]
    [InlineData(AttributeDataType.Boolean, "kW")]
    public void TheUnit_MustFitTheType(AttributeDataType type, string? unit)
    {
        var act = () => Attr(type, unit);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void ARange_IsRefused_ByASingleValueAttribute()
    {
        var act = () => Attr(AttributeDataType.Number, "kW").AddValue(Guid.NewGuid(), "0.5–2 kW", "0.5–2 kW", 0);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void VeryLongText_IsRefusedAsInvalid_NotAServerError()
    {
        // Refused before the text is scanned at all.
        var act = () => UnitCatalog.Parse("1 kW" + new string(' ', 100_000), "kW", allowRange: false);
        act.Should().Throw<ValidationException>().WithMessage("*150*");

        // Text attributes are not parsed, so the length rule must hold on its own (the column holds 150 characters).
        var thread = new AttributeDefinition(Guid.NewGuid(), "Body thread", "থ্রেড", "body_thread", AttributeDataType.String, null, true);
        var tooLong = () => thread.AddValue(Guid.NewGuid(), new string('x', 151), "x", 0);
        tooLong.Should().Throw<ValidationException>().WithMessage("*150*");
    }

    [Fact]
    public void AnUnknownAttributeType_IsRefused()
    {
        var act = () => Attr((AttributeDataType)99, null);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void TheSameValue_CannotBeAddedTwice()
    {
        var thread = new AttributeDefinition(Guid.NewGuid(), "Body thread", "থ্রেড", "body_thread", AttributeDataType.String, null, true);
        thread.AddValue(Guid.NewGuid(), "M12", "M12", 0);
        var act = () => thread.AddValue(Guid.NewGuid(), " m12 ", "m12", 1);
        act.Should().Throw<ValidationException>();
    }
}
