using FluentAssertions;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Pricing;

namespace ShopVerse.UnitTests.Pricing;

public class PromotionEngineTests
{
    private static readonly DateTime Now = new(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
    private static readonly Guid CategoryA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid CategoryB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");
    private static readonly Guid SellerX = Guid.Parse("c0000000-0000-0000-0000-00000000000c");
    private static readonly Guid SellerY = Guid.Parse("d0000000-0000-0000-0000-00000000000d");

    private static CartLine Line(int n, int qty, long unitMinor, Guid? category = null, Guid? seller = null) =>
        new(Guid.Parse($"00000000-0000-0000-0000-{n:D12}"), Guid.Parse($"11111111-0000-0000-0000-{n:D12}"),
            seller ?? SellerX, category ?? CategoryA, qty, unitMinor);

    private static Promotion Percent(int id, decimal pct, int priority = 100, bool exclusive = false, PromotionScope? scope = null, long? cap = null, long min = 0) =>
        new(Guid.Parse($"90000000-0000-0000-0000-{id:D12}"), $"P{id}", PromotionKind.PercentOff, scope ?? PromotionScope.Everything,
            priority, exclusive, Percent: pct, MaxDiscountMinor: cap, MinSubtotalMinor: min);

    private static Promotion Fixed(int id, long minor, int priority = 100) =>
        new(Guid.Parse($"90000000-0000-0000-0000-{id:D12}"), $"F{id}", PromotionKind.FixedOff, PromotionScope.Everything, priority, FixedMinor: minor);

    private static void AssertBooksBalance(IReadOnlyList<CartLine> lines, PromotionResult r)
    {
        r.TotalDiscountMinor.Should().Be(r.Lines.Sum(l => l.DiscountMinor));
        r.TotalDiscountMinor.Should().Be(r.Applied.Sum(a => a.DiscountMinor), "what is applied is exactly what the lines carry");
        for (var i = 0; i < lines.Count; i++)
        {
            r.Lines[i].DiscountMinor.Should().BeInRange(0, lines[i].GrossMinor, "a line can never be discounted below zero");
        }
    }

    [Fact]
    public void PercentOff_IsRoundedOnce_ByBankersRounding()
    {
        // 25 poisha x 10 % = 2.5 -> 2 (half to even); 35 x 10 % = 3.5 -> 4.
        var a = PromotionEngine.Apply([Line(1, 1, 25)], [Percent(1, 10)], Now);
        var b = PromotionEngine.Apply([Line(1, 1, 35)], [Percent(1, 10)], Now);
        a.TotalDiscountMinor.Should().Be(2);
        b.TotalDiscountMinor.Should().Be(4);
    }

    [Fact]
    public void FixedOff_NeverExceedsWhatTheBasketIsWorth()
    {
        var lines = new[] { Line(1, 1, 5_000) };
        var r = PromotionEngine.Apply(lines, [Fixed(1, 99_999)], Now);
        r.TotalDiscountMinor.Should().Be(5_000);
        AssertBooksBalance(lines, r);
    }

    [Fact]
    public void MaxDiscountCap_LimitsTheDiscount_AndTheSplitStillBalances()
    {
        var lines = new[] { Line(1, 2, 100_000), Line(2, 1, 50_000) };
        var r = PromotionEngine.Apply(lines, [Percent(1, 50, cap: 30_001)], Now);
        r.TotalDiscountMinor.Should().Be(30_001);
        AssertBooksBalance(lines, r);
    }

    [Fact]
    public void Allocation_LosesNothing_WhenThePennyDoesNotSplitEvenly()
    {
        // 1 poisha discount over three equal lines: exactly one line gets it, the earliest on a tie.
        var lines = new[] { Line(1, 1, 100), Line(2, 1, 100), Line(3, 1, 100) };
        var r = PromotionEngine.Apply(lines, [Fixed(1, 1)], Now);
        r.Lines.Select(l => l.DiscountMinor).Should().Equal(1, 0, 0);

        var r2 = PromotionEngine.Apply(lines, [Fixed(1, 100)], Now);
        r2.Lines.Select(l => l.DiscountMinor).Should().Equal(34, 33, 33);
        AssertBooksBalance(lines, r2);
    }

    [Fact]
    public void Allocate_GivesTheLeftoverToTheLargestRemainder_NotToTheFirstLine()
    {
        // 1 poisha over weights 10:90 -> exact shares 0.1 and 0.9, so the second line is owed it.
        PromotionEngine.Allocate(1, [10, 90], [100, 100]).Should().Equal(0, 1);
        // 10 over three equal weights: 3.33 each, the single leftover goes to the earliest on a tie.
        PromotionEngine.Allocate(10, [1, 1, 1], [10, 10, 10]).Should().Equal(4, 3, 3);
        // 2 over weights 1:1:2 -> exact 0.5, 0.5, 1.0: floors give 0,0,1; the two equal remainders tie, the earlier wins.
        PromotionEngine.Allocate(2, [1, 1, 2], [2, 2, 2]).Should().Equal(1, 0, 1);
    }

    [Fact]
    public void Allocate_RespectsEachLinesLimit_AndStillBalancesWhenItCan()
    {
        // The heavy line can only absorb 3; the rest must land on the other line.
        PromotionEngine.Allocate(10, [100, 1], [3, 50]).Should().Equal(3, 7);
        // More wanted than all limits together: gives everything there is, never more.
        PromotionEngine.Allocate(100, [1, 1], [5, 5]).Should().Equal(5, 5);
    }

    [Fact]
    public void VolumeTier_UsesTheHighestBreakEachLineReaches()
    {
        var tier = new Promotion(Guid.Parse("90000000-0000-0000-0000-000000000001"), "Bulk", PromotionKind.VolumeTier, PromotionScope.Everything,
            VolumeBreaks: [new VolumeBreak(5, 5m), new VolumeBreak(20, 10m), new VolumeBreak(100, 15m)]);
        var lines = new[] { Line(1, 4, 10_000), Line(2, 5, 10_000), Line(3, 25, 10_000), Line(4, 100, 1_000) };

        var r = PromotionEngine.Apply(lines, [tier], Now);

        r.Lines.Select(l => l.DiscountMinor).Should().Equal(
            0,          // below the first break
            2_500,      // 5 % of 50,000
            25_000,     // 10 % of 250,000
            15_000);    // 15 % of 100,000
        AssertBooksBalance(lines, r);
    }

    [Fact]
    public void SpendTier_NeedsTheThreshold_AndPicksTheHighestOneReached()
    {
        var tier = new Promotion(Guid.Parse("90000000-0000-0000-0000-000000000001"), "Spend", PromotionKind.SpendTier, PromotionScope.Everything,
            SpendBreaks: [new SpendBreak(100_000, 2m), new SpendBreak(500_000, 5m)]);

        PromotionEngine.Apply([Line(1, 1, 99_999)], [tier], Now).Rejected.Single().Reason.Should().Be(PromotionEngine.NoEffect);
        PromotionEngine.Apply([Line(1, 1, 100_000)], [tier], Now).TotalDiscountMinor.Should().Be(2_000);
        PromotionEngine.Apply([Line(1, 5, 100_000)], [tier], Now).TotalDiscountMinor.Should().Be(25_000);
    }

    [Fact]
    public void Scope_OnlyDiscountsMatchingLines()
    {
        var lines = new[] { Line(1, 1, 10_000, CategoryA, SellerX), Line(2, 1, 10_000, CategoryB, SellerY) };

        PromotionEngine.Apply(lines, [Percent(1, 10, scope: new(PromotionScopeKind.Category, CategoryB))], Now)
            .Lines.Select(l => l.DiscountMinor).Should().Equal(0, 1_000);
        PromotionEngine.Apply(lines, [Percent(1, 10, scope: new(PromotionScopeKind.Seller, SellerX))], Now)
            .Lines.Select(l => l.DiscountMinor).Should().Equal(1_000, 0);
        PromotionEngine.Apply(lines, [Percent(1, 10, scope: new(PromotionScopeKind.Variant, lines[1].VariantId))], Now)
            .Lines.Select(l => l.DiscountMinor).Should().Equal(0, 1_000);
        PromotionEngine.Apply(lines, [Percent(1, 10, scope: new(PromotionScopeKind.Category, Guid.NewGuid()))], Now)
            .Rejected.Single().Reason.Should().Be(PromotionEngine.NoEligibleLines);
    }

    [Fact]
    public void MinimumSpend_IsMeasuredOnTheEligibleLines_BeforeAnyDiscount()
    {
        var lines = new[] { Line(1, 1, 60_000), Line(2, 1, 60_000, CategoryB) };
        var gated = Percent(2, 10, priority: 200, scope: new(PromotionScopeKind.Category, CategoryA), min: 100_000);
        var first = Percent(1, 50, priority: 100);

        // The 50 % promotion runs first, but the gate for the category promotion still looks at the 60,000 the buyer picked.
        var r = PromotionEngine.Apply(lines, [first, gated], Now);
        r.Rejected.Single().Reason.Should().Be(PromotionEngine.BelowMinimumSpend);
    }

    [Fact]
    public void Window_AndUsageLimits_AreHonoured()
    {
        var lines = new[] { Line(1, 1, 10_000) };
        var id = Guid.Parse("90000000-0000-0000-0000-000000000001");
        var future = Percent(1, 10) with { StartsAtUtc = Now.AddMinutes(1) };
        var past = Percent(1, 10) with { EndsAtUtc = Now };           // end is exclusive
        var limited = Percent(1, 10) with { GlobalLimit = 100, PerUserLimit = 1 };

        PromotionEngine.Apply(lines, [future], Now).Rejected.Single().Reason.Should().Be(PromotionEngine.NotYetValid);
        PromotionEngine.Apply(lines, [past], Now).Rejected.Single().Reason.Should().Be(PromotionEngine.Expired);
        PromotionEngine.Apply(lines, [limited], Now, new Dictionary<Guid, PromotionUsage> { [id] = new(100, 0) })
            .Rejected.Single().Reason.Should().Be(PromotionEngine.GlobalLimitReached);
        PromotionEngine.Apply(lines, [limited], Now, new Dictionary<Guid, PromotionUsage> { [id] = new(3, 1) })
            .Rejected.Single().Reason.Should().Be(PromotionEngine.PerUserLimitReached);
        PromotionEngine.Apply(lines, [limited], Now, new Dictionary<Guid, PromotionUsage> { [id] = new(99, 0) })
            .TotalDiscountMinor.Should().Be(1_000);
    }

    [Fact]
    public void Stacking_AppliesInPriorityOrder_EachOnWhatTheEarlierOnesLeft()
    {
        var lines = new[] { Line(1, 1, 100_000) };
        // 10 % first -> 90,000 left; then 10 % of that = 9,000; total 19,000 (not 20,000).
        var r = PromotionEngine.Apply(lines, [Percent(2, 10, priority: 2), Percent(1, 10, priority: 1)], Now);
        r.Applied.Select(a => a.DiscountMinor).Should().Equal(10_000, 9_000);
        r.TotalDiscountMinor.Should().Be(19_000);
    }

    [Fact]
    public void TheOrderOfTheInputListNeverChangesTheAnswer()
    {
        var lines = new[] { Line(1, 3, 33_333), Line(2, 7, 12_345, CategoryB) };
        var promos = new[] { Percent(1, 12.5m, 1), Fixed(2, 777, 2), Percent(3, 3m, 2, scope: new(PromotionScopeKind.Category, CategoryB)) };
        var expected = PromotionEngine.Apply(lines, promos, Now);

        foreach (var permutation in new[] { new[] { 2, 1, 0 }, new[] { 1, 0, 2 }, new[] { 0, 2, 1 } })
        {
            var r = PromotionEngine.Apply(lines, permutation.Select(i => promos[i]).ToArray(), Now);
            r.Lines.Should().Equal(expected.Lines);
            r.Applied.Should().Equal(expected.Applied);
        }
    }

    [Fact]
    public void Exclusive_BlocksOthers_InBothDirections()
    {
        var lines = new[] { Line(1, 1, 100_000) };
        var exclusive = Percent(1, 20, priority: 1, exclusive: true);
        var other = Percent(2, 10, priority: 2);

        var r = PromotionEngine.Apply(lines, [exclusive, other], Now);
        r.TotalDiscountMinor.Should().Be(20_000);
        r.Rejected.Single().Should().Be(new RejectedPromotion(other.Id, PromotionEngine.NotStackable));

        var reversed = PromotionEngine.Apply(lines, [Percent(1, 10, priority: 1), Percent(2, 20, priority: 2, exclusive: true)], Now);
        reversed.Applied.Should().ContainSingle();
        reversed.Rejected.Single().Reason.Should().Be(PromotionEngine.NotStackable);
    }

    [Fact]
    public void BadInput_IsRefused()
    {
        Action zeroQty = () => PromotionEngine.Apply([Line(1, 0, 100)], [], Now);
        Action negative = () => PromotionEngine.Apply([Line(1, 1, -1)], [], Now);
        Action pct = () => PromotionEngine.Apply([Line(1, 1, 100)], [Percent(1, 101)], Now);
        zeroQty.Should().Throw<ValidationException>();
        negative.Should().Throw<ValidationException>();
        pct.Should().Throw<ValidationException>();
    }

    [Fact]
    public void RandomBaskets_AlwaysBalanceToThePoisha()
    {
        var rng = new Random(20261101);
        for (var round = 0; round < 500; round++)
        {
            var lines = Enumerable.Range(1, rng.Next(1, 8))
                .Select(n => Line(n, rng.Next(1, 40), rng.Next(0, 2_000_000), rng.Next(2) == 0 ? CategoryA : CategoryB, rng.Next(2) == 0 ? SellerX : SellerY))
                .ToArray();
            var promos = new List<Promotion>
            {
                Percent(1, rng.Next(0, 101), 1, cap: rng.Next(2) == 0 ? rng.Next(0, 3_000_000) : null),
                Fixed(2, rng.Next(0, 4_000_000), 2),
                new(Guid.Parse("90000000-0000-0000-0000-000000000003"), "Bulk", PromotionKind.VolumeTier, new(PromotionScopeKind.Category, CategoryB), 3,
                    VolumeBreaks: [new VolumeBreak(5, 3.3m), new VolumeBreak(20, 7.7m)]),
                new(Guid.Parse("90000000-0000-0000-0000-000000000004"), "Spend", PromotionKind.SpendTier, PromotionScope.Everything, 4,
                    SpendBreaks: [new SpendBreak(1_000_000, 1.1m), new SpendBreak(10_000_000, 9.9m)])
            };

            var r = PromotionEngine.Apply(lines, promos, Now);

            AssertBooksBalance(lines, r);
            r.TotalDiscountMinor.Should().BeLessThanOrEqualTo(lines.Sum(l => l.GrossMinor));
        }
    }
}
