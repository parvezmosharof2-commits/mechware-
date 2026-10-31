using FluentAssertions;
using ShopVerse.Domain.Cart;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.UnitTests.Cart;

public class CartEngineTests
{
    private static Guid V(int n) => Guid.Parse($"00000000-0000-0000-0000-{n:D12}");

    private static VariantFacts Facts(int n, long price = 10_000, int available = 50, bool sellable = true, int moq = 1, int pack = 1) =>
        new(V(n), sellable, price, available, moq, pack);

    private static ShopVerse.Domain.Cart.Cart CartOf(params (int Variant, int Qty, long Price)[] lines) =>
        new(lines.Select(l => new CartItem(V(l.Variant), l.Qty, l.Price)).ToList());

    [Fact]
    public void Add_CreatesTheLine_AndAddingAgainAddsUp()
    {
        var cart = CartEngine.Add(ShopVerse.Domain.Cart.Cart.Empty, Facts(1), 2);
        cart = CartEngine.Add(cart, Facts(1), 3);
        cart.Items.Should().ContainSingle().Which.Quantity.Should().Be(5);
        cart.Version.Should().Be(2);
    }

    [Fact]
    public void Add_RefusesWhatCannotBeBought()
    {
        var empty = ShopVerse.Domain.Cart.Cart.Empty;
        ((Action)(() => CartEngine.Add(empty, Facts(1), 0))).Should().Throw<ValidationException>();
        ((Action)(() => CartEngine.Add(empty, Facts(1, sellable: false), 1))).Should().Throw<ConflictException>().Which.Code.Should().Be("VARIANT_UNAVAILABLE");
        ((Action)(() => CartEngine.Add(empty, Facts(1, available: 3), 4))).Should().Throw<ConflictException>().Which.Code.Should().Be("INSUFFICIENT_STOCK");
        ((Action)(() => CartEngine.Add(empty, Facts(1, available: 100_000), CartEngine.MaxQuantityPerLine + 1))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void AddingTwice_CannotSneakPastTheStockLimit()
    {
        var cart = CartEngine.Add(ShopVerse.Domain.Cart.Cart.Empty, Facts(1, available: 5), 3);
        ((Action)(() => CartEngine.Add(cart, Facts(1, available: 5), 3))).Should().Throw<ConflictException>().Which.Code.Should().Be("INSUFFICIENT_STOCK");
    }

    [Fact]
    public void MinimumOrder_AndPackSize_AreEnforced()
    {
        var empty = ShopVerse.Domain.Cart.Cart.Empty;
        var f = Facts(1, moq: 10, pack: 5);
        ((Action)(() => CartEngine.Set(empty, f, 5))).Should().Throw<ValidationException>().WithMessage("*minimum*");
        ((Action)(() => CartEngine.Set(empty, f, 12))).Should().Throw<ValidationException>().WithMessage("*packs of 5*");
        CartEngine.Set(empty, f, 15).Items.Single().Quantity.Should().Be(15);
    }

    [Fact]
    public void Set_ToZero_Removes_AndUnknownRemoveIsANoOp()
    {
        var cart = CartOf((1, 2, 100), (2, 1, 100));
        CartEngine.Set(cart, Facts(1), 0).Items.Select(i => i.VariantId).Should().Equal(V(2));
        CartEngine.Remove(cart, V(99)).Should().BeSameAs(cart);
    }

    [Fact]
    public void CartHoldsAtMostMaxLines()
    {
        var cart = ShopVerse.Domain.Cart.Cart.Empty;
        for (var n = 1; n <= CartEngine.MaxLines; n++) cart = CartEngine.Add(cart, Facts(n), 1);
        ((Action)(() => CartEngine.Add(cart, Facts(CartEngine.MaxLines + 1), 1))).Should().Throw<ConflictException>().Which.Code.Should().Be("CART_FULL");
        CartEngine.Add(cart, Facts(1), 1).Items.Should().HaveCount(CartEngine.MaxLines);   // an existing line still grows
    }

    [Fact]
    public void Merge_AddsQuantities_KeepsTheBuyersOrder_AndAppendsNewGuestLines()
    {
        var user = CartOf((1, 2, 100), (2, 1, 100));
        var guest = CartOf((3, 4, 100), (1, 5, 100));

        var merged = CartEngine.Merge(user, guest);

        merged.Cart.Items.Select(i => (i.VariantId, i.Quantity)).Should().Equal((V(1), 7), (V(2), 1), (V(3), 4));
        merged.Notices.Should().BeEmpty();
    }

    [Fact]
    public void Merge_CapsAQuantity_AndSaysSo()
    {
        var user = CartOf((1, CartEngine.MaxQuantityPerLine - 1, 100));
        var merged = CartEngine.Merge(user, CartOf((1, 5, 100)));

        merged.Cart.Items.Single().Quantity.Should().Be(CartEngine.MaxQuantityPerLine);
        merged.Notices.Should().ContainSingle(n => n.Kind == CartNoticeKind.QuantityCapped);
    }

    [Fact]
    public void Merge_WithAnEmptyGuest_ChangesNothingButTheVersion()
    {
        var user = CartOf((1, 2, 100));
        CartEngine.Merge(user, ShopVerse.Domain.Cart.Cart.Empty).Cart.Items.Should().Equal(user.Items);
    }

    [Fact]
    public void Revalidate_WhenNothingDrifted_ReturnsTheSameCart_AndNoNotices()
    {
        var cart = CartOf((1, 2, 10_000));
        var result = CartEngine.Revalidate(cart, new Dictionary<Guid, VariantFacts> { [V(1)] = Facts(1, 10_000) });
        result.Cart.Should().BeSameAs(cart);
        result.Notices.Should().BeEmpty();
    }

    [Fact]
    public void Revalidate_ReportsEveryKindOfDrift_AndFixesTheCart()
    {
        var cart = CartOf(
            (1, 2, 10_000),   // price went up
            (2, 8, 5_000),    // stock fell to 3
            (3, 1, 5_000),    // sold out
            (4, 1, 5_000),    // withdrawn
            (5, 1, 5_000),    // not in the catalog answer at all
            (6, 7, 5_000),    // pack of 5: 7 -> 5
            (7, 6, 5_000));   // trimmed to stock 4, then below its MOQ of 10

        var facts = new Dictionary<Guid, VariantFacts>
        {
            [V(1)] = Facts(1, 12_500),
            [V(2)] = Facts(2, 5_000, available: 3),
            [V(3)] = Facts(3, available: 0),
            [V(4)] = Facts(4, sellable: false),
            [V(6)] = Facts(6, 5_000, pack: 5),
            [V(7)] = Facts(7, 5_000, available: 4, moq: 10)
        };

        var result = CartEngine.Revalidate(cart, facts);

        result.Cart.Items.Select(i => (i.VariantId, i.Quantity, i.SeenUnitNetMinor)).Should().Equal(
            (V(1), 2, 12_500L), (V(2), 3, 5_000L), (V(6), 5, 5_000L));
        result.Notices.Select(n => (n.Kind, n.VariantId)).Should().BeEquivalentTo(new[]
        {
            (CartNoticeKind.PriceChanged, V(1)),
            (CartNoticeKind.StockReduced, V(2)),
            (CartNoticeKind.OutOfStock, V(3)),
            (CartNoticeKind.Unavailable, V(4)),
            (CartNoticeKind.Unavailable, V(5)),
            (CartNoticeKind.QuantityAdjustedToPack, V(6)),
            (CartNoticeKind.StockReduced, V(7)),
            (CartNoticeKind.BelowMinimumOrder, V(7))
        });
        result.Notices.Single(n => n.Kind == CartNoticeKind.PriceChanged).Should().Be(new CartNotice(CartNoticeKind.PriceChanged, V(1), 10_000, 12_500));
    }

    [Fact]
    public void Revalidate_IsIdempotent_TheSecondPassSaysNothing()
    {
        var cart = CartOf((1, 8, 10_000), (2, 1, 5_000));
        var facts = new Dictionary<Guid, VariantFacts> { [V(1)] = Facts(1, 11_000, available: 3), [V(2)] = Facts(2, available: 0) };

        var first = CartEngine.Revalidate(cart, facts);
        var second = CartEngine.Revalidate(first.Cart, facts);

        first.Notices.Should().NotBeEmpty();
        second.Notices.Should().BeEmpty();
        second.Cart.Should().BeSameAs(first.Cart);
    }
}
