using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.UnitTests.Catalog;

/// <summary>Sprint 5–6: part numbers are found however they are typed (brief: exact hits ≥ 98% "any formatting").</summary>
public class PartNumberTests
{
    [Theory]
    [InlineData("6ES7214-1AG40-0XB0", "6ES72141AG400XB0")]
    [InlineData("6ES7 214-1AG40-0XB0", "6ES72141AG400XB0")]
    [InlineData("6es7214-1ag40-0xb0", "6ES72141AG400XB0")]
    [InlineData("6ES7.214.1AG40.0XB0", "6ES72141AG400XB0")]
    [InlineData("  6ES7_214/1AG40\\0XB0  ", "6ES72141AG400XB0")]
    [InlineData("FR-D720S-0.4K", "FRD720S04K")]
    [InlineData("৬ES7২১৪-1AG40-0XB0", "6ES72141AG400XB0")]      // Bangla digits typed on a Bangla keyboard
    [InlineData("E3Z-D61 2M", "E3ZD612M")]
    [InlineData("Ünï-cödé", "NCD")]                               // letters outside A–Z are not part of a key
    [InlineData("--- ///", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void TheKey_IsLettersAndDigitsOnly_InUpperCase(string? raw, string key) =>
        PartNumbers.Normalise(raw).Should().Be(key);

    [Fact]
    public void TheKey_IsCutAtSixtyFourCharacters()
    {
        PartNumbers.Normalise(new string('a', 500) + "1").Should().Be(new string('A', 64));
        PartNumbers.Normalise(string.Join("-", Enumerable.Repeat("AB", 100))).Length.Should().Be(64);
    }

    [Theory]
    [InlineData("6ES72141AG400XB0", "6ES72141AG400XB0")]
    [InlineData("6ES7214IAG4OOXBO", "6ES72141AG400XB0")]   // O typed for 0, I for 1
    [InlineData("LC1D09", "1C1D09")]
    [InlineData("OIL", "011")]
    public void TheFuzzyKey_ReadsLookAlikeLettersAsDigits(string key, string fuzzy) =>
        PartNumbers.Fuzzy(key).Should().Be(fuzzy);

    [Theory]
    [InlineData("6ES7214-1AG40-0XB0", true)]
    [InlineData("6es7 214 1ag40", true)]
    [InlineData("LC1D09", true)]
    [InlineData("siemens", false)]               // no digit: a word
    [InlineData("inverter 2 hp", false)]         // a number among words is not a part number
    [InlineData("2900011", true)]                // an all-digit part number (Phoenix Contact)
    [InlineData("24v", false)]                   // too short a key
    [InlineData("3RT2015-1BB41", true)]
    [InlineData("vfd", false)]
    [InlineData("12", false)]                    // too short
    [InlineData("a b c d e f", false)]           // many words
    [InlineData("siemens s7 1200 plc cpu", false)]   // a sentence that happens to hold a model: five words
    [InlineData("6ES7 214 1AG40 0XB0", true)]        // four groups is still a part number
    [InlineData("", false)]
    [InlineData(null, false)]
    public void APartNumberLookingQuery_HasADigit_AFewCharacters_AndFewWords(string? text, bool looksLike) =>
        PartNumbers.LooksLikePartNumber(text).Should().Be(looksLike);

    private static ProductVariant Variant() =>
        new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "CPU", "সিপিইউ", "cpu-" + Guid.NewGuid().ToString("N")[..6], "s", "s", "<p>d</p>")
            .AddVariant(Guid.NewGuid(), "SKU-1", null, new Dictionary<string, string>(), new Money(1000, Currency.BDT), null, null, 0.4m, true);

    [Fact]
    public void AVariant_KeepsThePartNumberAsPrinted_AndBothKeys()
    {
        var v = Variant();
        v.SetManufacturerPartNumber("  6ES7214-1AG40-0XB0 ");
        (v.ManufacturerPartNumber, v.PartNumberKey, v.PartNumberFuzzyKey).Should().Be(("6ES7214-1AG40-0XB0", "6ES72141AG400XB0", "6ES72141AG400XB0"));

        v.SetManufacturerPartNumber("LC1-D09");
        (v.PartNumberKey, v.PartNumberFuzzyKey).Should().Be(("LC1D09", "1C1D09"));

        v.SetManufacturerPartNumber("  ");
        (v.ManufacturerPartNumber, v.PartNumberKey, v.PartNumberFuzzyKey).Should().Be((null, null, null));
    }

    [Theory]
    [InlineData("A")]                 // one letter: could never be told apart
    [InlineData("-/-.")]              // nothing usable
    [InlineData("AB\u0007CD")]         // a control character
    public void AVariant_RefusesAPartNumberThatCouldNotBeFound_OrIsNotPrintable(string partNumber)
    {
        var v = Variant();
        v.SetManufacturerPartNumber("ORIGINAL-1");
        var act = () => v.SetManufacturerPartNumber(partNumber);
        act.Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("manufacturerPartNumber");
        v.ManufacturerPartNumber.Should().Be("ORIGINAL-1", "a refused value changes nothing");
    }

    [Fact]
    public void AVariant_RefusesAPartNumberLongerThanSixtyFourCharacters()
    {
        var v = Variant();
        ((Action)(() => v.SetManufacturerPartNumber(new string('A', 64)))).Should().NotThrow();
        ((Action)(() => v.SetManufacturerPartNumber(new string('A', 65)))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void ThePartNumber_CannotBeChanged_WhileTheProductIsUnderReview()
    {
        var p = new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "CPU", "সিপিইউ", "cpu-review", "s", "s", "<p>d</p>");
        var v = p.AddVariant(Guid.NewGuid(), "SKU-R", null, new Dictionary<string, string>(), new Money(1000, Currency.BDT), null, null, 0.4m, true);
        p.AddMedia(Guid.NewGuid(), null, "/img/a.jpg", "front view", 1200, 1200);
        p.SubmitForReview();

        var act = () => p.SetManufacturerPartNumber(v.Id, "6ES7214-1AG40-0XB0");
        act.Should().Throw<ValidationException>();
        v.ManufacturerPartNumber.Should().BeNull();
    }

    [Fact]
    public void SettingThePartNumber_RaisesTheUpdateEvent_SoThePageAndTheIndexFollow()
    {
        var p = new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "CPU", "সিপিইউ", "cpu-event", "s", "s", "<p>d</p>");
        var v = p.AddVariant(Guid.NewGuid(), "SKU-E", null, new Dictionary<string, string>(), new Money(1000, Currency.BDT), null, null, 0.4m, true);
        p.ClearDomainEvents();

        p.SetManufacturerPartNumber(v.Id, "6ES7214-1AG40-0XB0");

        p.DomainEvents.Should().ContainSingle(e => e is ProductUpdatedEvent);
        ((Action)(() => p.SetManufacturerPartNumber(Guid.NewGuid(), "X1"))).Should().Throw<ValidationException>();
    }
}
