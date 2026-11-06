using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.UnitTests.Catalog;

/// <summary>Sprint 3–4 (PIM): manufacturer lifecycle, successor, alternatives, minimum order quantity and pack size.</summary>
public class ProductPimRulesTests
{
    private static Product NewProduct() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Siemens S7-1200 CPU 1214C", "সিমেন্স",
            "s7-1200-" + Guid.NewGuid().ToString("N")[..8], "s", "s", "<p>d</p>");

    private static ProductVariant NewVariant() =>
        NewProduct().AddVariant(Guid.NewGuid(), "TB-2.5", null, new Dictionary<string, string>(),
            new Money(25m, Currency.BDT), null, null, 0.01m, true);

    [Fact]
    public void ANewProduct_IsActive_WithoutASuccessor()
    {
        var p = NewProduct();
        (p.Lifecycle, p.SuccessorProductId, p.SuccessorIsDropIn, p.SuccessorNote).Should().Be((ProductLifecycle.Active, null, false, null));
    }

    [Fact]
    public void AnObsoletePart_NamesItsSuccessor_WithWhatChanges()
    {
        var p = NewProduct();
        var successor = Guid.NewGuid();

        p.SetLifecycle(ProductLifecycle.Obsolete, successor, successorIsDropIn: false, successorNote: "  Firmware V4: migrate the TIA Portal project.  ");

        (p.Lifecycle, p.SuccessorProductId, p.SuccessorIsDropIn).Should().Be((ProductLifecycle.Obsolete, successor, false));
        p.SuccessorNote.Should().Be("Firmware V4: migrate the TIA Portal project.");
    }

    [Fact]
    public void AnActivePart_CannotHaveASuccessor()
    {
        var act = () => NewProduct().SetLifecycle(ProductLifecycle.Active, Guid.NewGuid());
        act.Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("successorProductId");
    }

    [Fact]
    public void APart_CannotSucceedItself()
    {
        var p = NewProduct();
        var act = () => p.SetLifecycle(ProductLifecycle.Nrnd, p.Id);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void CompatibilityNotes_NeedASuccessor()
    {
        var p = NewProduct();
        ((Action)(() => p.SetLifecycle(ProductLifecycle.Obsolete, null, successorIsDropIn: true))).Should().Throw<ValidationException>();
        ((Action)(() => p.SetLifecycle(ProductLifecycle.Obsolete, null, successorNote: "x"))).Should().Throw<ValidationException>();
        p.SetLifecycle(ProductLifecycle.Obsolete, null);   // obsolete with no known successor is fine
        p.Lifecycle.Should().Be(ProductLifecycle.Obsolete);
    }

    [Fact]
    public void ATooLongNote_IsRefused()
    {
        var act = () => NewProduct().SetLifecycle(ProductLifecycle.Nrnd, Guid.NewGuid(), false, new string('x', Product.MaxSuccessorNoteLength + 1));
        act.Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("successorNote");
    }

    [Fact]
    public void GoingBackToActive_ClearsTheSuccessor()
    {
        var p = NewProduct();
        p.SetLifecycle(ProductLifecycle.Nrnd, Guid.NewGuid(), true, "same footprint");
        p.SetLifecycle(ProductLifecycle.Active, null);
        (p.SuccessorProductId, p.SuccessorIsDropIn, p.SuccessorNote).Should().Be((null, false, null));
    }

    [Fact]
    public void AnUnknownLifecycle_IsRefused()
    {
        var act = () => NewProduct().SetLifecycle((ProductLifecycle)99, null);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Alternatives_AreKeptInOrder_WithoutDuplicates_AndCanBeRemoved()
    {
        var p = NewProduct();
        Guid a = Guid.NewGuid(), b = Guid.NewGuid();
        p.AddAlternative(a);
        p.AddAlternative(b);
        p.AddAlternative(a);

        p.Alternatives.Select(x => (x.AlternativeProductId, x.Position)).Should().Equal((a, 0), (b, 1));

        p.RemoveAlternative(a);
        p.Alternatives.Select(x => x.AlternativeProductId).Should().Equal(b);
    }

    [Fact]
    public void APart_IsNotItsOwnAlternative()
    {
        var p = NewProduct();
        ((Action)(() => p.AddAlternative(p.Id))).Should().Throw<ValidationException>();
        ((Action)(() => p.AddAlternative(Guid.Empty))).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Alternatives_AreCapped()
    {
        var p = NewProduct();
        for (int i = 0; i < Product.MaxAlternatives; i++) p.AddAlternative(Guid.NewGuid());
        var act = () => p.AddAlternative(Guid.NewGuid());
        act.Should().Throw<ValidationException>();
        p.Alternatives.Should().HaveCount(Product.MaxAlternatives);
    }

    [Fact]
    public void ANewVariant_SellsSingleUnits()
    {
        var v = NewVariant();
        (v.MinOrderQty, v.PackSize).Should().Be((1, 1));
        v.QuantityProblem(1).Should().BeNull();
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
    public void OrderQuantities_FollowTheMinimumAndThePack(int min, int pack, int quantity, bool allowed)
    {
        var v = NewVariant();
        v.SetOrderingRules(min, pack);
        (v.QuantityProblem(quantity) is null).Should().Be(allowed);
    }

    [Fact]
    public void TheProblem_SaysWhichRuleAndWhichSku()
    {
        var v = NewVariant();
        v.SetOrderingRules(100, 50);
        v.QuantityProblem(50).Should().Contain("TB-2.5").And.Contain("minimum order is 100");
        v.QuantityProblem(120).Should().Contain("packs of 50");
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-5, 1)]
    [InlineData(25, 10)]   // the minimum must be whole packs
    [InlineData(1, 10)]
    [InlineData(ProductVariant.MaxOrderingUnit + 1, 1)]
    [InlineData(1, ProductVariant.MaxOrderingUnit + 1)]
    public void InvalidOrderingRules_AreRefused_AndChangeNothing(int min, int pack)
    {
        var v = NewVariant();
        var act = () => v.SetOrderingRules(min, pack);
        act.Should().Throw<ValidationException>();
        (v.MinOrderQty, v.PackSize).Should().Be((1, 1));
    }
}
