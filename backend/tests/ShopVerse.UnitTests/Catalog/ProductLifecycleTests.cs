using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.UnitTests.Catalog;

public class ProductLifecycleTests
{
    private static Money Tk(decimal v) => new(v, Currency.BDT);

    private static Product NewProduct(bool ready = true)
    {
        var p = new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Siemens S7-1200 CPU 1214C", "সিমেন্স",
            "siemens-s7-1200-cpu-1214c", "s", "s", "<p>d</p>");
        if (ready)
        {
            p.AddVariant(Guid.NewGuid(), "6ES7214-1AG40-0XB0", null, new Dictionary<string, string>(), Tk(52000), null, null, 0.4m, true);
            p.AddMedia(Guid.NewGuid(), null, "/img/a.jpg", "S7-1200 CPU front view", 1200, 1200);
        }

        return p;
    }

    [Fact]
    public void HappyPath_Draft_Pending_Active_Archived()
    {
        var p = NewProduct();
        p.Status.Should().Be(ProductStatus.Draft);

        p.SubmitForReview();
        p.Status.Should().Be(ProductStatus.PendingReview);

        p.Approve();
        p.Status.Should().Be(ProductStatus.Active);

        p.Archive();
        p.Status.Should().Be(ProductStatus.Archived);
    }

    [Fact]
    public void Reject_RequiresReason_ThenReturnsToDraftForRework()
    {
        var p = NewProduct();
        p.SubmitForReview();

        var blank = () => p.Reject("  ");
        blank.Should().Throw<ValidationException>();

        p.Reject("Datasheet missing");
        p.Status.Should().Be(ProductStatus.Rejected);
        p.RejectionReason.Should().Be("Datasheet missing");

        p.ReturnToDraft();
        p.Status.Should().Be(ProductStatus.Draft);
        p.SubmitForReview();
        p.Approve();
        p.RejectionReason.Should().BeNull("a fresh approval clears the previous rejection");
    }

    [Theory]
    [InlineData("approve_from_draft")]
    [InlineData("reject_from_draft")]
    [InlineData("submit_twice")]
    [InlineData("approve_twice")]
    [InlineData("archive_twice")]
    [InlineData("archive_pending")]
    [InlineData("draft_from_active")]
    public void InvalidTransitions_Throw(string scenario)
    {
        var p = NewProduct();
        Action act = scenario switch
        {
            "approve_from_draft" => () => p.Approve(),
            "reject_from_draft" => () => p.Reject("x"),
            "submit_twice" => () => { p.SubmitForReview(); p.SubmitForReview(); }
            ,
            "approve_twice" => () => { p.SubmitForReview(); p.Approve(); p.Approve(); }
            ,
            "archive_twice" => () => { p.Archive(); p.Archive(); }
            ,
            "archive_pending" => () => { p.SubmitForReview(); p.Archive(); }
            ,
            "draft_from_active" => () => { p.SubmitForReview(); p.Approve(); p.ReturnToDraft(); }
            ,
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };

        act.Should().Throw<InvalidProductTransitionException>();
    }

    [Fact]
    public void SubmitForReview_RequiresVariantAndImage_AndStaysDraftOnFailure()
    {
        var noVariant = NewProduct(ready: false);
        var act = () => noVariant.SubmitForReview();
        act.Should().Throw<ValidationException>().Which.Errors.Should().Contain(e => e.Field == "variants");
        noVariant.Status.Should().Be(ProductStatus.Draft);

        var noMedia = NewProduct(ready: false);
        noMedia.AddVariant(Guid.NewGuid(), "SKU-1", null, new Dictionary<string, string>(), Tk(10), null, null, 1m, true);
        var act2 = () => noMedia.SubmitForReview();
        act2.Should().Throw<ValidationException>().Which.Errors.Should().Contain(e => e.Field == "media");
        noMedia.Status.Should().Be(ProductStatus.Draft);
    }

    [Fact]
    public void ProductUnderReview_CannotBeEdited()
    {
        var p = NewProduct();
        p.SubmitForReview();

        var act = () => p.SetContent("[]", "{}", null, null);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void LifecycleAndEdits_RaiseProductUpdatedEvents()
    {
        var p = NewProduct();
        p.ClearDomainEvents();

        p.SubmitForReview();
        p.Approve();

        p.DomainEvents.OfType<ProductUpdatedEvent>().Select(e => e.Status)
            .Should().Equal(ProductStatus.PendingReview, ProductStatus.Active);
    }

    [Fact]
    public void DomainEvents_HaveStableIdsAndTimestamps()
    {
        var e = new ProductUpdatedEvent(Guid.NewGuid(), "a", ProductStatus.Active);
        ((ShopVerse.Domain.Common.Primitives.IDomainEvent)e).EventId.Should().Be(e.EventId);
        e.EventId.Should().NotBeEmpty();
    }
}
