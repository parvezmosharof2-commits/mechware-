using FluentAssertions;
using Mechware.Domain.Cart;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Identity;

namespace Mechware.UnitTests.Catalog;

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
        var facts = Mechware.Domain.Cart.VariantFacts.From(v, sellable: true, unitNetMinor: 100_00, available: 1000);

        var cart = new Mechware.Domain.Cart.Cart([]);
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

    [Fact]
    public void ASuccessor_CarriesWhetherItFitsAsItIs_AndWhatChanges()
    {
        var old = Published("old-cpu");
        var successor = Published("new-cpu");
        old.SetSupply(SupplyStatus.Obsolete, successor, successorIsDropIn: false, successorNote: "  Firmware V4: migrate the project.  ");
        (old.SuccessorIsDropIn, old.SuccessorNote).Should().Be((false, "Firmware V4: migrate the project."));

        old.SetSupply(SupplyStatus.Active, null);
        (old.SuccessorProductId, old.SuccessorIsDropIn, old.SuccessorNote).Should().Be((null, false, null));
    }

    [Fact]
    public void CompatibilityNotes_NeedASuccessor_AndAreBounded()
    {
        var p = Published("lonely");
        ((Action)(() => p.SetSupply(SupplyStatus.Obsolete, null, successorIsDropIn: true))).Should().Throw<ValidationException>();
        ((Action)(() => p.SetSupply(SupplyStatus.Obsolete, null, successorNote: "x"))).Should().Throw<ValidationException>();
        var tooLong = () => p.SetSupply(SupplyStatus.Nrnd, Published("next"), false, new string('x', Product.MaxSuccessorNoteLength + 1));
        tooLong.Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("successorNote");
    }

    [Fact]
    public void Alternatives_AreKeptInOrder_WithoutDuplicates_AndCanBeRemoved()
    {
        var p = Published("plc");
        Product a = Published("alt-a", Guid.NewGuid()), b = Published("alt-b");   // another seller is fine for an alternative
        p.AddAlternative(a);
        p.AddAlternative(b);
        p.AddAlternative(a);

        p.Alternatives.Select(x => (x.AlternativeProductId, x.Position)).Should().Equal((a.Id, 0), (b.Id, 1));
        p.RemoveAlternative(a.Id);
        p.Alternatives.Select(x => x.AlternativeProductId).Should().Equal(b.Id);
    }

    [Fact]
    public void AnAlternative_IsAnotherPublishedProduct_AndTheListIsCapped()
    {
        var p = Published("hmi");
        ((Action)(() => p.AddAlternative(p))).Should().Throw<ValidationException>();
        var draft = new Product(Guid.NewGuid(), Seller, Guid.NewGuid(), null, "Draft", "খসড়া", "draft-x", "s", "s", "<p>d</p>");
        ((Action)(() => p.AddAlternative(draft))).Should().Throw<ValidationException>();

        for (int i = 0; i < Product.MaxAlternatives; i++) p.AddAlternative(Published($"alt-{i}"));
        ((Action)(() => p.AddAlternative(Published("one-more")))).Should().Throw<ValidationException>();
        p.Alternatives.Should().HaveCount(Product.MaxAlternatives);
    }

    [Fact]
    public void RemovingAnAlternative_ClosesTheGap_SoTheNextOneNeverTiesInPosition()
    {
        var p = Published("switch");
        Product a = Published("pos-a"), b = Published("pos-b"), c = Published("pos-c");
        p.AddAlternative(a);
        p.AddAlternative(b);
        p.RemoveAlternative(a.Id);
        p.AddAlternative(c);

        p.Alternatives.Select(x => (x.AlternativeProductId, x.Position)).Should().Equal((b.Id, 0), (c.Id, 1));
    }

    [Fact]
    public void ChangingOnlyTheStatus_KeepsTheSuccessorsNoteAndDropInFlag()
    {
        var old = Published("keep-old");
        var successor = Published("keep-new");
        old.SetSupply(SupplyStatus.Nrnd, successor, successorIsDropIn: true, successorNote: "Same footprint.");

        old.SetSupply(SupplyStatus.Obsolete, successor);   // a status change that says nothing about the note

        (old.SupplyStatus, old.SuccessorIsDropIn, old.SuccessorNote).Should().Be((SupplyStatus.Obsolete, true, "Same footprint."));
    }

    [Fact]
    public void ADifferentSuccessor_StartsWithoutTheOldNote_AndABlankNoteClearsIt()
    {
        var old = Published("swap-old");
        old.SetSupply(SupplyStatus.Nrnd, Published("swap-a"), true, "About A.");

        old.SetSupply(SupplyStatus.Nrnd, Published("swap-b"));
        (old.SuccessorIsDropIn, old.SuccessorNote).Should().Be((false, null));

        var same = Published("swap-c");
        old.SetSupply(SupplyStatus.Nrnd, same, true, "About C.");
        old.SetSupply(SupplyStatus.Nrnd, same, successorNote: "   ");
        (old.SuccessorIsDropIn, old.SuccessorNote).Should().Be((true, null), "only the note was cleared");
    }

    [Fact]
    public void AnObsoleteProduct_CannotBeAddedAsAnAlternative()
    {
        var p = Published("cpu");
        var gone = Published("gone");
        gone.SetSupply(SupplyStatus.Obsolete, null);

        var act = () => p.AddAlternative(gone);
        act.Should().Throw<ValidationException>().Which.Errors.Single().Message.Should().Contain("not obsolete");
        p.Alternatives.Should().BeEmpty();
    }

    [Fact]
    public void AlternativesCannotBeChanged_WhileTheProductIsUnderReview()
    {
        var p = new Product(Guid.NewGuid(), Seller, Guid.NewGuid(), null, "Under review", "পর্যালোচনায়", "under-review", "s", "s", "<p>d</p>");
        p.AddVariant(Guid.NewGuid(), "SKU-UR", null, new Dictionary<string, string>(), new Money(1000, Currency.BDT), null, null, 0.4m, true);
        p.AddMedia(Guid.NewGuid(), null, "/img/a.jpg", "front view", 1200, 1200);
        var other = Published("ur-other");
        p.AddAlternative(other);
        p.SubmitForReview();

        ((Action)(() => p.AddAlternative(Published("ur-two")))).Should().Throw<ValidationException>();
        ((Action)(() => p.RemoveAlternative(other.Id))).Should().Throw<ValidationException>();
        p.Alternatives.Should().HaveCount(1);
    }

    [Fact]
    public void IsLinkable_IsTrueOnlyForAPublishedNonObsoleteProduct()
    {
        var test = Product.IsLinkable.Compile();
        var live = Published("live");
        test(live).Should().BeTrue();

        var nrnd = Published("nrnd");
        nrnd.SetSupply(SupplyStatus.Nrnd, null);
        test(nrnd).Should().BeTrue("still sold");

        var obsolete = Published("obs");
        obsolete.SetSupply(SupplyStatus.Obsolete, null);
        test(obsolete).Should().BeFalse();

        var archived = Published("arch");
        archived.Archive();
        test(archived).Should().BeFalse();

        var deleted = Published("del");
        deleted.SoftDelete(DateTime.UtcNow);
        test(deleted).Should().BeFalse();
    }

    [Fact]
    public void ReplaceAlternatives_SetsTheWholeList_InOrder_KeepingRowsThatStay()
    {
        var p = Published("main-part");
        Product a = Published("ra-a"), b = Published("ra-b"), c = Published("ra-c");
        p.ReplaceAlternatives([a, b]);
        var rowOfB = p.Alternatives.Single(x => x.AlternativeProductId == b.Id);

        p.ReplaceAlternatives([c, b]);

        p.Alternatives.Select(x => (x.AlternativeProductId, x.Position)).Should().Equal((c.Id, 0), (b.Id, 1));
        p.Alternatives.Single(x => x.AlternativeProductId == b.Id).Should().BeSameAs(rowOfB, "a row that stays is moved, not recreated");

        p.ReplaceAlternatives([]);
        p.Alternatives.Should().BeEmpty();
    }

    [Fact]
    public void ReplaceAlternatives_RefusesBadLists_AndChangesNothing()
    {
        var p = Published("main-two");
        Product a = Published("rb-a"), gone = Published("rb-gone");
        gone.SetSupply(SupplyStatus.Obsolete, null);
        p.ReplaceAlternatives([a]);

        ((Action)(() => p.ReplaceAlternatives([a, a]))).Should().Throw<ValidationException>();
        ((Action)(() => p.ReplaceAlternatives([p]))).Should().Throw<ValidationException>();
        ((Action)(() => p.ReplaceAlternatives([gone]))).Should().Throw<ValidationException>();
        ((Action)(() => p.ReplaceAlternatives([.. Enumerable.Range(0, Product.MaxAlternatives + 1).Select(i => Published($"rb-{i}"))])))
            .Should().Throw<ValidationException>();

        p.Alternatives.Select(x => x.AlternativeProductId).Should().Equal(a.Id);
    }

    [Fact]
    public void ReplaceAlternatives_WithTheSameList_RaisesNoEvent()
    {
        var p = Published("main-same");
        var a = Published("rs-a");
        p.ReplaceAlternatives([a]);
        p.ClearDomainEvents();

        p.ReplaceAlternatives([a]);

        p.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void OnlyAdministrators_CanManageTheCatalogData()
    {
        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Support, Permissions.Roles.Moderator, Permissions.Roles.Finance })
            Permissions.GetDefaultPermissionsForRole(role).Should().NotContain(Permissions.CatalogManage, role);
        Permissions.GetDefaultPermissionsForRole(Permissions.Roles.Admin).Should().Contain(Permissions.CatalogManage);
    }
}
