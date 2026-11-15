using FluentAssertions;
using Mechware.Domain.Cart;

namespace Mechware.UnitTests.Cart;

/// <summary>D5.2: the quick order fits a quantity to a variant's order units with the rules the cart enforces.</summary>
public class OrderUnitFitTests
{
    [Theory]
    [InlineData(1, 1, 1, 1, null)]
    [InlineData(7, 1, 1, 7, null)]
    [InlineData(1, 10, 1, 10, CartNoticeKind.BelowMinimumOrder)]
    [InlineData(7, 1, 5, 10, CartNoticeKind.QuantityAdjustedToPack)]
    [InlineData(10, 1, 5, 10, null)]
    [InlineData(3, 10, 5, 10, CartNoticeKind.BelowMinimumOrder)]      // min is whole packs: raising to it is enough
    [InlineData(12, 10, 5, 15, CartNoticeKind.QuantityAdjustedToPack)]
    public void FitToOrderUnits_RaisesToTheMinimum_ThenUpToWholePacks(int requested, int min, int pack, int expected, CartNoticeKind? notice)
    {
        var fit = CartEngine.FitToOrderUnits(requested, min, pack);
        fit.Quantity.Should().Be(expected);
        fit.Notice.Should().Be(notice);
    }

    [Fact]
    public void FitToOrderUnits_NeverReturnsLessThanOne_AndSurvivesHugeValues()
    {
        CartEngine.FitToOrderUnits(0, 1, 1).Quantity.Should().Be(1);
        CartEngine.FitToOrderUnits(int.MaxValue, 1, 1_000).Quantity.Should().Be(int.MaxValue);
    }

    [Fact]
    public void FitToOrderUnits_ResultIsAlwaysAcceptedByTheCartsOwnRules()
    {
        for (int pack = 1; pack <= 12; pack++)
            for (int packsInMin = 1; packsInMin <= 4; packsInMin++)
                for (int requested = 1; requested <= 60; requested++)
                {
                    int min = pack * packsInMin;
                    var fit = CartEngine.FitToOrderUnits(requested, min, pack);
                    var facts = new VariantFacts(Guid.NewGuid(), true, 100, 10_000, min, pack);
                    var cart = CartEngine.Set(Mechware.Domain.Cart.Cart.Empty, facts, fit.Quantity);   // throws if the cart would refuse it
                    cart.Items.Single().Quantity.Should().Be(fit.Quantity);
                    fit.Quantity.Should().BeGreaterThanOrEqualTo(requested);
                    (fit.Notice is null).Should().Be(fit.Quantity == requested);
                }
    }
}
