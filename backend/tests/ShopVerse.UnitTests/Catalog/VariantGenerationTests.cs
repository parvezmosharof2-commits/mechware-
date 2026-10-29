using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.UnitTests.Catalog;

public class VariantGenerationTests
{
    private static Money Tk(decimal v) => new(v, Currency.BDT);

    private static Product NewProduct() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Xiaomi Redmi Note 14", "রেডমি", "xiaomi-redmi-note-14", "s", "s", "d");

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Axes(params (string, string[])[] axes) =>
        axes.ToDictionary(a => a.Item1, a => (IReadOnlyList<string>)a.Item2);

    [Fact]
    public void GeneratesCartesianProduct_OfAllOptionAxes()
    {
        var p = NewProduct();
        var created = p.GenerateVariants("RN14",
            Axes(("Color", ["Black", "Blue"]), ("Storage", ["128GB", "256GB", "512GB"])), Tk(25000));

        created.Should().HaveCount(6);
        p.Variants.Select(v => v.Sku).Should().BeEquivalentTo(
            "RN14-BLACK-128GB", "RN14-BLACK-256GB", "RN14-BLACK-512GB", "RN14-BLUE-128GB", "RN14-BLUE-256GB", "RN14-BLUE-512GB");
        p.Variants.Select(v => v.OptionValues["Color"] + v.OptionValues["Storage"]).Distinct().Should().HaveCount(6);
    }

    [Fact]
    public void FirstVariantIsDefault_ExactlyOneDefault()
    {
        var p = NewProduct();
        p.GenerateVariants("TEE", Axes(("Size", ["S", "M", "L", "XL"])), Tk(900));

        p.Variants.Count(v => v.IsDefault).Should().Be(1);
        p.Variants.Single(v => v.IsDefault).OptionValues["Size"].Should().Be("S");
    }

    [Fact]
    public void GenerationIsIdempotent_AndOnlyAddsMissingCombinations()
    {
        var p = NewProduct();
        var axes = Axes(("Color", ["Black", "White"]));
        p.GenerateVariants("CASE", axes, Tk(500));

        p.GenerateVariants("CASE", axes, Tk(500)).Should().BeEmpty();
        p.Variants.Should().HaveCount(2);

        var extended = Axes(("Color", ["Black", "White", "Red"]));
        p.GenerateVariants("CASE", extended, Tk(500)).Should().ContainSingle(v => v.OptionValues["Color"] == "Red");
        p.Variants.Should().HaveCount(3);
    }

    [Fact]
    public void PriceResolver_AllowsPerVariantPricing()
    {
        var p = NewProduct();
        p.GenerateVariants("PH", Axes(("Storage", ["128GB", "256GB"])), Tk(20000),
            combo => combo["Storage"] == "256GB" ? Tk(24000) : Tk(20000));

        p.Variants.Single(v => v.OptionValues["Storage"] == "256GB").Price.Should().Be(Tk(24000));
        p.Variants.Single(v => v.OptionValues["Storage"] == "128GB").Price.Should().Be(Tk(20000));
    }

    [Fact]
    public void SkuNormalisesSpecialCharacters()
    {
        var p = NewProduct();
        p.GenerateVariants("mw-sie", Axes(("Voltage", ["24V DC", "230V AC"])), Tk(100));
        p.Variants.Select(v => v.Sku).Should().BeEquivalentTo("MW-SIE-24VDC", "MW-SIE-230VAC");
    }

    [Fact]
    public void EmptyAxes_Throw()
    {
        var p = NewProduct();
        var none = () => p.GenerateVariants("X", Axes(), Tk(1));
        none.Should().Throw<ValidationException>();
        var emptyAxis = () => p.GenerateVariants("X", Axes(("Color", [])), Tk(1));
        emptyAxis.Should().Throw<ValidationException>();
    }

    [Fact]
    public void DuplicateSkuOrOptions_AreRejected()
    {
        var p = NewProduct();
        p.AddVariant(Guid.NewGuid(), "SKU-1", null, new Dictionary<string, string> { ["A"] = "1" }, Tk(10), null, null, 1m, true);

        var dupSku = () => p.AddVariant(Guid.NewGuid(), "sku-1", null, new Dictionary<string, string> { ["A"] = "2" }, Tk(10), null, null, 1m, false);
        dupSku.Should().Throw<ValidationException>();

        var dupOptions = () => p.AddVariant(Guid.NewGuid(), "SKU-2", null, new Dictionary<string, string> { ["A"] = "1" }, Tk(10), null, null, 1m, false);
        dupOptions.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Variant_PriceStoredAsMinorUnits_AndSaleScheduleValidated()
    {
        var p = NewProduct();
        var v = p.AddVariant(Guid.NewGuid(), "S1", null, new Dictionary<string, string>(), Tk(1250.50m), Tk(1500m), null, 1m, true);
        v.PriceMinor.Should().Be(125_050);
        v.CompareAtPriceMinor.Should().Be(150_000);

        var start = new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);
        var tooHigh = () => v.ScheduleSale(Tk(1300m), start, start.AddDays(1));
        tooHigh.Should().Throw<ValidationException>();
        var backwards = () => v.ScheduleSale(Tk(1000m), start, start);
        backwards.Should().Throw<ValidationException>();

        v.ScheduleSale(Tk(1000m), start, start.AddDays(1));
        v.SalePriceMinor.Should().Be(100_000);
        v.ClearSale();
        v.SalePrice.Should().BeNull();
    }
}
