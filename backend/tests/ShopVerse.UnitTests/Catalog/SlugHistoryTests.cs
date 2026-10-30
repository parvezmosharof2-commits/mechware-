using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.UnitTests.Catalog;

public class SlugHistoryTests
{
    private static Product NewProduct(string slug = "siemens-s7-1200") =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Siemens S7-1200", "সিমেন্স", slug, "s", "s", "d");

    [Theory]
    [InlineData("Siemens SIMATIC S7-1200 (CPU 1214C)", "siemens-simatic-s7-1200-cpu-1214c")]
    [InlineData("  Schneider   Electric  ", "schneider-electric")]
    [InlineData("Café Müller & Söhne", "cafe-muller-sohne")]
    [InlineData("24V DC / 5A", "24v-dc-5a")]
    public void Slug_From_ProducesUrlSafeSlug(string text, string expected) =>
        Slug.From(text).Should().Be(expected);

    [Theory]
    [InlineData("Has Space")]
    [InlineData("UPPER")]
    [InlineData("double--hyphen")]
    [InlineData("-leading")]
    [InlineData("")]
    public void Slug_Require_RejectsInvalidSlugs(string slug)
    {
        var act = () => Slug.Require(slug);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void ChangeSlug_ReturnsRetiredSlugHistory_AndRaisesEvent()
    {
        var p = NewProduct("old-slug");
        p.ClearDomainEvents();

        var history = p.ChangeSlug("new-slug", Guid.NewGuid());

        p.Slug.Should().Be("new-slug");
        history.Should().NotBeNull();
        history!.OldSlug.Should().Be("old-slug");
        history.EntityId.Should().Be(p.Id);
        history.EntityType.Should().Be(SlugEntityType.Product);
        p.DomainEvents.OfType<ProductSlugChangedEvent>().Should().ContainSingle(e => e.OldSlug == "old-slug" && e.NewSlug == "new-slug");
    }

    [Fact]
    public void ChangeSlug_ToSameSlug_IsNoOp()
    {
        var p = NewProduct("same");
        p.ClearDomainEvents();
        p.ChangeSlug("same", Guid.NewGuid()).Should().BeNull();
        p.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void RetiredSlug_RedirectsToCurrentSlug_EvenAfterMultipleRenames()
    {
        var p = NewProduct("a");
        var histories = new List<SlugHistory> { p.ChangeSlug("b", Guid.NewGuid())!, p.ChangeSlug("c", Guid.NewGuid())! };

        var live = new Dictionary<string, Guid> { [p.Slug] = p.Id };
        var current = new Dictionary<Guid, string> { [p.Id] = p.Slug };
        var retired = histories.ToDictionary(h => h.OldSlug, h => h.EntityId);

        var fromA = SlugResolver.Resolve("a", live, current, retired);
        fromA.Kind.Should().Be(SlugResolutionKind.Redirect);
        fromA.CurrentSlug.Should().Be("c", "A→B→C collapses to a single 301 hop");

        SlugResolver.Resolve("b", live, current, retired).CurrentSlug.Should().Be("c");
        SlugResolver.Resolve("c", live, current, retired).Kind.Should().Be(SlugResolutionKind.Current);
        SlugResolver.Resolve("zzz", live, current, retired).Kind.Should().Be(SlugResolutionKind.NotFound);
    }

    [Fact]
    public void LiveSlug_WinsOverStaleHistory_WhenSlugIsReused()
    {
        var original = NewProduct("shared");
        var history = original.ChangeSlug("renamed", Guid.NewGuid())!;
        var newcomer = NewProduct("shared");   // someone later takes the freed slug

        var live = new Dictionary<string, Guid> { [newcomer.Slug] = newcomer.Id, [original.Slug] = original.Id };
        var current = new Dictionary<Guid, string> { [newcomer.Id] = newcomer.Slug, [original.Id] = original.Slug };
        var retired = new Dictionary<string, Guid> { [history.OldSlug] = history.EntityId };

        var result = SlugResolver.Resolve("shared", live, current, retired);
        result.Kind.Should().Be(SlugResolutionKind.Current);
        result.EntityId.Should().Be(newcomer.Id);
    }

    [Fact]
    public void Category_MaterializedPath_ReflectsHierarchy_AndSlugChangeRebasesDescendants()
    {
        var root = Category.CreateRoot(Guid.NewGuid(), "Industrial Automation", "ইন্ডাস্ট্রিয়াল অটোমেশন", "industrial-automation");
        var sub = Category.CreateChild(root, Guid.NewGuid(), "PLCs", "পিএলসি", "plcs");
        var leaf = Category.CreateChild(sub, Guid.NewGuid(), "Compact PLCs", "কমপ্যাক্ট পিএলসি", "compact-plcs");

        root.MaterializedPath.Should().Be("root.industrial-automation");
        leaf.MaterializedPath.Should().Be("root.industrial-automation.plcs.compact-plcs");
        (root.Depth, sub.Depth, leaf.Depth).Should().Be((0, 1, 2));
        root.IsAncestorOf(leaf).Should().BeTrue();
        leaf.IsAncestorOf(root).Should().BeFalse();

        var oldPrefix = root.MaterializedPath;
        var history = root.ChangeSlug("automation", Guid.NewGuid());
        history!.OldSlug.Should().Be("industrial-automation");
        leaf.RebasePath(oldPrefix, root.MaterializedPath);
        leaf.MaterializedPath.Should().Be("root.automation.plcs.compact-plcs");
    }

    [Fact]
    public void Category_RejectsFourthLevel_AndInheritsVatFromParent()
    {
        var root = Category.CreateRoot(Guid.NewGuid(), "A", "এ", "a", vatRate: 0.15m);
        var l2 = Category.CreateChild(root, Guid.NewGuid(), "B", "বি", "b");
        var l3 = Category.CreateChild(l2, Guid.NewGuid(), "C", "সি", "c");
        l3.VatRate.Should().Be(0.15m);

        var act = () => Category.CreateChild(l3, Guid.NewGuid(), "D", "ডি", "d");
        act.Should().Throw<ValidationException>();
    }
}
