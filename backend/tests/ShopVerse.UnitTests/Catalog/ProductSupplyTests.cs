using FluentAssertions;
using ShopVerse.Domain.Cart;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.UnitTests.Catalog;

/// <summary>
/// PIM (sprint 3–4): a product's supply status (active / not recommended for new designs / obsolete), the product that
/// replaces it, and the order units of each SKU (minimum order quantity and pack size).
/// </summary>
public class ProductSupplyTests
{
    private static readonly Guid Seller = Guid.NewGuid();

    private static Product Published(string slug, Guid? seller = null)
    {
        var p = new Product(Guid.NewGuid(), seller ?? Seller, Guid.NewGuid(), null, "Title " + slug, "শিরোনাম", slug, "s", "s", "<p>d</p>");
        p.AddVariant(Guid.NewGuid(), "SKU-" + slug, null, new Dictionary<string, string>(), new Money(1000, Currency.BDT), null, null, 0.4m, true);
        p.AddMedia(Guid.NewGuid(), null, "/img/a.jpg", "front view", 1200, 1200);
        p.SubmitForReview();
        p.Approve();
        return p;
    }

    [Fact]
    public void ANewProduct_IsInSupply_WithNoSuccessor()
    {
        var p = Published("cpu-old");
        p.SupplyStatus.Should().Be(SupplyStatus.Active);
        p.SuccessorProductId.Should().BeNull();
    }

    [Theory]
    [InlineData(SupplyStatus.Nrnd)]
    [InlineData(SupplyStatus.Obsolete)]
    public void AnEndOfLifeProduct_CanNameItsSuccessor(SupplyStatus status)
    {
        var old = Published("cpu-old");
        var successor = Published("cpu-new");

        old.SetSupply(status, successor);

        old.SupplyStatus.Should().Be(status);
        old.SuccessorProductId.Should().Be(successor.Id);
    }

    [Fact]
    public void AnEndOfLifeProduct_MayHaveNoSuccessor()
    {
        var old = Published("cpu-old");
        old.SetSupply(SupplyStatus.Obsolete, null);
        old.SuccessorProductId.Should().BeNull();
    }

    [Fact]
    public void AnActiveProduct_CannotHaveASuccessor()
    {
        var old = Published("cpu-old");
        var act = () => old.SetSupply(SupplyStatus.Active, Published("cpu-new"));
        act.Should().Throw<ValidationException>().WithMessage("*successor*");
    }

    [Fact]
    public void BackInSupply_ClearsTheSuccessor()
    {
        var old = Published("cpu-old");
        old.SetSupply(SupplyStatus.Nrnd, Published("cpu-new"));

        old.SetSupply(SupplyStatus.Active, null);

        old.SupplyStatus.Should().Be(SupplyStatus.Active);
        old.SuccessorProductId.Should().BeNull();
    }

    [Fact]
    public void AnUnknownSupplyStatus_IsRefused()
    {
        var act = () => Published("cpu-old").SetSupply((SupplyStatus)99, null);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void AProduct_CannotSucceedItself()
    {
        var old = Published("cpu-old");
        var act = () => old.SetSupply(SupplyStatus.Obsolete, old);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void TheSuccessor_MustBeTheSameSellersProduct()
    {
        // Otherwise one seller could steer another seller's buyers to their own catalogue.
        var old = Published("cpu-old");
        var act = () => old.SetSupply(SupplyStatus.Obsolete, Published("cpu-new", seller: Guid.NewGuid()));
        act.Should().Throw<ValidationException>().WithMessage("*same seller*");
    }

    [Fact]
    public void TheSuccessor_MustBeOnSale_AndNotItselfEndOfLife()
    {
        var old = Published("cpu-old");

        var draft = new Product(Guid.NewGuid(), Seller, Guid.NewGuid(), null, "Draft", "খসড়া", "cpu-draft", "s", "s", "<p>d</p>");
        var notPublished = () => old.SetSupply(SupplyStatus.Obsolete, draft);
        notPublished.Should().Throw<ValidationException>();

        var obsolete = Published("cpu-obsolete");
        obsolete.SetSupply(SupplyStatus.Obsolete, null);
        var endOfLife = () => old.SetSupply(SupplyStatus.Obsolete, obsolete);
        endOfLife.Should().Throw<ValidationException>();
    }

    [Fact]
    public void ASku_SellsOneAtATime_ByDefault()
    {
        var v = Published("cpu-old").Variants.Single();
        (v.MinOrderQty, v.PackSize).Should().Be((1, 1));
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(20, 10)]
    [InlineData(5, 1)]
    public void OrderUnits_AreKept_WhenTheMinimumIsWholePacks(int moq, int pack)
    {
        var v = Published("cable").Variants.Single();
        v.SetOrderUnits(moq, pack);
        (v.MinOrderQty, v.PackSize).Should().Be((moq, pack));
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-5, 1)]
    [InlineData(15, 10)]
    [InlineData(CartEngine.MaxQuantityPerLine + 1, 1)]
    [InlineData(1, CartEngine.MaxQuantityPerLine + 1)]
    public void OrderUnits_ThatNoCartCouldHonour_AreRefused(int moq, int pack)
    {
        var v = Published("cable").Variants.Single();
        var act = () => v.SetOrderUnits(moq, pack);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void ChangingSupplyOrOrderUnits_RefreshesTheProductPage()
    {
        var p = Published("cpu-old");
        p.ClearDomainEvents();
        p.SetSupply(SupplyStatus.Nrnd, null);
        p.DomainEvents.Should().ContainSingle(e => e is ProductUpdatedEvent);

        p.ClearDomainEvents();
        p.SetOrderUnits(p.Variants.Single().Id, 10, 5);
        p.DomainEvents.Should().ContainSingle(e => e is ProductUpdatedEvent);
        p.Variants.Single().MinOrderQty.Should().Be(10);

        var unknown = () => p.SetOrderUnits(Guid.NewGuid(), 1, 1);
        unknown.Should().Throw<ValidationException>();
    }

    [Fact]
    public void TheCart_HonoursTheSkusOrderUnits()
    {
        var v = Published("cable").Variants.Single();
        v.SetOrderUnits(20, 10);
        var facts = v.ToCartFacts(sellable: true, unitNetMinor: 100_00, available: 1000);

        var cart = new ShopVerse.Domain.Cart.Cart([]);
        var below = () => CartEngine.Set(cart, facts, 10);
        below.Should().Throw<ValidationException>().WithMessage("*minimum*20*");
        var notAPack = () => CartEngine.Set(cart, facts, 25);
        notAPack.Should().Throw<ValidationException>().WithMessage("*packs of 10*");
        CartEngine.Set(cart, facts, 30).Items.Single().Quantity.Should().Be(30);
    }

    [Theory]
    [InlineData(50, 50, 50, true)]
    [InlineData(50, 50, 100, true)]
    [InlineData(50, 50, 49, false)]   // below the minimum
    [InlineData(50, 50, 75, false)]   // not whole packs
    [InlineData(100, 50, 50, false)]  // one pack, but the minimum is two
    [InlineData(100, 50, 150, true)]
    [InlineData(3, 1, 2, false)]
    [InlineData(3, 1, 4, true)]
    public void AnOrderLine_FollowsTheMinimumAndThePack(int min, int pack, int quantity, bool allowed)
    {
        var v = Published("units-" + Guid.NewGuid().ToString("N")[..6]).Variants.Single();
        v.SetOrderUnits(min, pack);
        (v.QuantityProblem(quantity) is null).Should().Be(allowed);
    }

    [Fact]
    public void AnOrderLineProblem_NamesTheSkuAndTheRule()
    {
        var v = Published("tb").Variants.Single();
        v.SetOrderUnits(100, 50);
        v.QuantityProblem(50).Should().Contain("SKU-TB").And.Contain("minimum order is 100");
        v.QuantityProblem(120).Should().Contain("packs of 50");
    }
}
