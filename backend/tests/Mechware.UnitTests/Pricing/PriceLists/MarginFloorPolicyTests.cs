using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Pricing.PriceLists;
using static Mechware.UnitTests.Pricing.PriceLists.PriceListBuilders;

namespace Mechware.UnitTests.Pricing.PriceLists;

/// <summary>D2.3: <see cref="MarginFloorPolicy"/> (ADR-0008 §4, owner decision 3; threat model §৮ PL10, PL11).</summary>
public class MarginFloorPolicyTests
{
    private static readonly Guid Brand = Guid.Parse("00000000-0000-0000-0000-0000000000d1");
    private static readonly Guid OtherBrand = Guid.Parse("00000000-0000-0000-0000-0000000000d2");
    private static readonly Guid Own = Guid.Parse("00000000-0000-0000-0000-0000000000e1");    // the product's category
    private static readonly Guid Parent = Guid.Parse("00000000-0000-0000-0000-0000000000e2"); // its parent
    private static readonly Guid Root = Guid.Parse("00000000-0000-0000-0000-0000000000e3");   // the root
    private static readonly Guid Unrelated = Guid.Parse("00000000-0000-0000-0000-0000000000e9");

    private static VariantCostFacts Facts(long? cost, Guid? brand = null, decimal vat = 0.05m) => new(V1, cost, brand, [Own, Parent, Root], vat);

    // ---- exit tests ---------------------------------------------------------------------------------------------------

    [Fact]
    public void EntryForAVariantWithoutCost_IsTreatedAsBelowFloor()
    {
        foreach (var facts in new[] { Facts(null), Facts(0), Facts(-5), Facts(PriceListLimits.MaxUnitPriceMinor + 1), null })
        {
            var e = MarginFloorPolicy.EvaluateEntry(Guid.NewGuid(), PriceListLimits.MaxUnitPriceMinor, facts, [Global10]);
            (e.BelowFloor, e.CostUnknown, e.BelowCost, e.FloorMinor, e.CostMinor, e.FloorSource)
                .Should().Be((true, true, false, (long?)null, (long?)null, (MarginFloorScope?)null), "no cost means below floor, however high the price");
        }

        var list = Draft();
        var entry = list.AddEntry(Guid.NewGuid(), V1, 1, 1_000_000, null, Author, Now);
        var other = list.AddEntry(Guid.NewGuid(), V2, 1, 1_000_000, null, Author, Now);
        var evaluation = MarginFloorPolicy.Evaluate(list, [new VariantCostFacts(V2, 1_000, null, [], 0.05m)], [Global10]); // V1 has no facts at all
        evaluation.BelowFloorEntryIds.Should().Equal(entry.Id);
        evaluation.Entries.Single(e => e.EntryId == other.Id).BelowFloor.Should().BeFalse();
    }

    [Fact]
    public void Floor_IsRoundedUp_NeverDown()
    {
        MarginFloorPolicy.FloorMinor(1, 10m).Should().Be(2, "1.1 poisha rounds up to 2");
        MarginFloorPolicy.FloorMinor(101, 10m).Should().Be(112, "111.1 → 112");
        MarginFloorPolicy.FloorMinor(115, 10m).Should().Be(127, "126.5 → 127 (banker's rounding would give 126)");
        MarginFloorPolicy.FloorMinor(100, 10m).Should().Be(110, "exact stays exact");
        MarginFloorPolicy.FloorMinor(3, 33.33m).Should().Be(4, "3.9999 → 4");
        MarginFloorPolicy.FloorMinor(99_999, 0.01m).Should().Be(100_009, "99,999 × 1.0001 = 100,008.9999 → 100,009");
        MarginFloorPolicy.FloorMinor(0, 10m).Should().Be(0);
        MarginFloorPolicy.FloorMinor(PriceListLimits.MaxUnitPriceMinor, MarginFloorPolicy.MaxMinMarkupPercent)
            .Should().Be(PriceListLimits.MaxUnitPriceMinor * 11, "no overflow at the limits");

        var atFloor = MarginFloorPolicy.EvaluateEntry(Guid.NewGuid(), 112, Facts(101), [Global10]);
        var below = MarginFloorPolicy.EvaluateEntry(Guid.NewGuid(), 111, Facts(101), [Global10]);
        (atFloor.BelowFloor, atFloor.FloorMinor).Should().Be((false, (long?)112));
        (below.BelowFloor, below.BelowCost).Should().Be((true, false), "111 is under the exact floor 111.1, so rounding never lets it pass");
    }

    [Fact]
    public void NoGlobalFloor_MeansEveryEntryIsBelowFloor()
    {
        var specificOnly = new[]
        {
            new MarginFloorRow(MarginFloorScope.Brand, Brand, null, 0m),
            new MarginFloorRow(MarginFloorScope.Category, null, Own, 0m),
            new MarginFloorRow(MarginFloorScope.BrandAndCategory, Brand, Own, 0m),
        };
        foreach (var rows in new[] { Array.Empty<MarginFloorRow>(), specificOnly })
        {
            var e = MarginFloorPolicy.EvaluateEntry(Guid.NewGuid(), PriceListLimits.MaxUnitPriceMinor, Facts(100, Brand), rows);
            (e.BelowFloor, e.CostUnknown, e.FloorMinor, e.FloorSource, e.CostMinor).Should().Be((true, false, (long?)null, (MarginFloorScope?)null, (long?)100));
            MarginFloorPolicy.FindFloor(Facts(100, Brand), rows).Should().BeNull();
        }

        var list = Draft();
        list.AddEntry(Guid.NewGuid(), V1, 1, 1_000_000, null, Author, Now);
        list.AddEntry(Guid.NewGuid(), V2, 1, 1_000_000, null, Author, Now);
        Floors(list, cost: 1, rows: []).BelowFloorEntryIds.Should().HaveCount(2);
        FluentActions.Invoking(() => list.Submit(Submitter, Floors(list, cost: 1, rows: []), false, Now)).Should().Throw<ValidationException>();
    }

    // ---- precedence -------------------------------------------------------------------------------------------------

    public static TheoryData<string, MarginFloorScope, decimal> Precedence => new()
    {
        // rows present (see Rows) → expected source and markup
        { "bc-own,bc-parent,brand,cat-own,cat-parent,global", MarginFloorScope.BrandAndCategory, 41m },
        { "bc-parent,brand,cat-own,cat-parent,global", MarginFloorScope.BrandAndCategory, 42m },
        { "bc-root,brand,global", MarginFloorScope.BrandAndCategory, 43m },
        { "brand,cat-own,cat-parent,global", MarginFloorScope.Brand, 30m },
        { "cat-own,cat-parent,global", MarginFloorScope.Category, 21m },
        { "cat-parent,cat-root,global", MarginFloorScope.Category, 22m },
        { "cat-root,global", MarginFloorScope.Category, 23m },
        { "global", MarginFloorScope.Global, 10m },
        { "other-brand,bc-other-brand,cat-unrelated,bc-unrelated,global", MarginFloorScope.Global, 10m },
        { "global,global-high", MarginFloorScope.Global, 12.5m },
        { "brand,brand-high,global", MarginFloorScope.Brand, 35m },
    };

    private static MarginFloorRow Row(string key) => key switch
    {
        "bc-own" => new(MarginFloorScope.BrandAndCategory, Brand, Own, 41m),
        "bc-parent" => new(MarginFloorScope.BrandAndCategory, Brand, Parent, 42m),
        "bc-root" => new(MarginFloorScope.BrandAndCategory, Brand, Root, 43m),
        "bc-other-brand" => new(MarginFloorScope.BrandAndCategory, OtherBrand, Own, 90m),
        "bc-unrelated" => new(MarginFloorScope.BrandAndCategory, Brand, Unrelated, 91m),
        "brand" => new(MarginFloorScope.Brand, Brand, null, 30m),
        "brand-high" => new(MarginFloorScope.Brand, Brand, null, 35m),
        "other-brand" => new(MarginFloorScope.Brand, OtherBrand, null, 92m),
        "cat-own" => new(MarginFloorScope.Category, null, Own, 21m),
        "cat-parent" => new(MarginFloorScope.Category, null, Parent, 22m),
        "cat-root" => new(MarginFloorScope.Category, null, Root, 23m),
        "cat-unrelated" => new(MarginFloorScope.Category, null, Unrelated, 93m),
        "global" => new(MarginFloorScope.Global, null, null, 10m),
        "global-high" => new(MarginFloorScope.Global, null, null, 12.5m),
        _ => throw new ArgumentOutOfRangeException(nameof(key)),
    };

    [Theory]
    [MemberData(nameof(Precedence))]
    public void Floor_MostSpecificRowWins_BrandAndCategory_Brand_Category_Global(string rows, MarginFloorScope source, decimal markup)
    {
        var floors = rows.Split(',').Select(Row).ToList();
        var forward = MarginFloorPolicy.FindFloor(Facts(10_000, Brand), floors);
        var backward = MarginFloorPolicy.FindFloor(Facts(10_000, Brand), Enumerable.Reverse(floors).ToList());

        (forward!.Scope, forward.MinMarkupPercent).Should().Be((source, markup));
        backward.Should().Be(forward, "row order does not matter");
        var e = MarginFloorPolicy.EvaluateEntry(Guid.NewGuid(), 1, Facts(10_000, Brand), floors);
        (e.FloorSource, e.FloorMinor).Should().Be(((MarginFloorScope?)source, (long?)MarginFloorPolicy.FloorMinor(10_000, markup)));
    }

    [Fact]
    public void Floor_WithoutABrand_SkipsTheBrandRows()
    {
        var floors = new[] { Row("bc-own"), Row("brand"), Row("cat-parent"), Row("global") };
        MarginFloorPolicy.FindFloor(Facts(10_000, brand: null), floors)!.MinMarkupPercent.Should().Be(22m);
        MarginFloorPolicy.FindFloor(new VariantCostFacts(V1, 10_000, null, [], 0m), floors)!.Scope.Should().Be(MarginFloorScope.Global);
    }

    // ---- inputs -----------------------------------------------------------------------------------------------------

    [Fact]
    public void GeneralEntry_IsComparedOnItsNetAmount_RoundedDown()
    {
        var list = Draft(PriceListScope.General);
        var exact = list.AddEntry(Guid.NewGuid(), V1, 2, 115_500, null, Author, Now);   // 115,500 / 1.05 = 110,000 net = the floor
        var under = list.AddEntry(Guid.NewGuid(), V1, 5, 115_499, null, Author, Now);   // 109,999.04 → 109,999 net
        var e = MarginFloorPolicy.Evaluate(list, [new VariantCostFacts(V1, 100_000, null, [], 0.05m)], [Global10]).Entries;

        e.Single(x => x.EntryId == exact.Id).Should().BeEquivalentTo(new { NetUnitPriceMinor = 110_000L, FloorMinor = (long?)110_000, BelowFloor = false });
        e.Single(x => x.EntryId == under.Id).Should().BeEquivalentTo(new { NetUnitPriceMinor = 109_999L, BelowFloor = true });

        var contract = Draft();
        var net = contract.AddEntry(Guid.NewGuid(), V1, 1, 110_000, null, Author, Now);
        MarginFloorPolicy.Evaluate(contract, [new VariantCostFacts(V1, 100_000, null, [], 0.05m)], [Global10]).Entries.Single()
            .Should().BeEquivalentTo(new { EntryId = net.Id, NetUnitPriceMinor = 110_000L, BelowFloor = false }, "a contract entry is already net");
    }

    [Fact]
    public void BadFloorRowsOrFacts_AreRefused_NotIgnored()
    {
        var bad = new[]
        {
            new MarginFloorRow(MarginFloorScope.Global, Brand, null, 10m),
            new MarginFloorRow(MarginFloorScope.Brand, null, null, 10m),
            new MarginFloorRow(MarginFloorScope.Category, Brand, Own, 10m),
            new MarginFloorRow(MarginFloorScope.BrandAndCategory, Brand, null, 10m),
            new MarginFloorRow((MarginFloorScope)7, null, null, 10m),
            new MarginFloorRow(MarginFloorScope.Global, null, null, -0.01m),
            new MarginFloorRow(MarginFloorScope.Global, null, null, 10.005m),
            new MarginFloorRow(MarginFloorScope.Global, null, null, 1_000.01m),
        };
        foreach (var row in bad)
            FluentActions.Invoking(() => MarginFloorPolicy.FindFloor(Facts(100), [Global10, row])).Should().Throw<ValidationException>();

        var list = Draft(PriceListScope.General);
        list.AddEntry(Guid.NewGuid(), V1, 2, 100_000, null, Author, Now);
        FluentActions.Invoking(() => MarginFloorPolicy.Evaluate(list, [Facts(100), Facts(200)], [Global10]))
            .Should().Throw<ValidationException>("a variant appears once");
        FluentActions.Invoking(() => MarginFloorPolicy.Evaluate(list, [Facts(100, vat: 0.6m)], [Global10])).Should().Throw<ValidationException>();
        MarginFloorPolicy.DefaultGlobalMinMarkupPercent.Should().Be(10m, "owner decision 3; D2.4 seeds it");
    }
}
