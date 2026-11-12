using FluentAssertions;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.UnitTests.Catalog;

/// <summary>The two domain methods the bulk import added: new titles and dropping an attribute value.</summary>
public class ProductImportEditTests
{
    private static Product NewProduct() =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Relay", "রিলে", "relay", "", "", "");

    [Fact]
    public void SetTitles_changes_both_titles_and_raises_an_update_event()
    {
        var p = NewProduct();
        p.ClearDomainEvents();
        p.SetTitles(" Relay 24V ", " রিলে ২৪ ");
        (p.TitleEn, p.TitleBn, p.Slug).Should().Be(("Relay 24V", "রিলে ২৪", "relay"));
        p.DomainEvents.Should().ContainSingle();
        FluentActions.Invoking(() => p.SetTitles(" ", "x")).Should().Throw<ValidationException>();
    }

    [Fact]
    public void RemoveAttributeValue_returns_the_link_once_and_nothing_after()
    {
        var p = NewProduct();
        var id = Guid.NewGuid();
        p.AssignAttributeValue(id);
        p.RemoveAttributeValue(id)!.AttributeValueId.Should().Be(id);
        p.AttributeValues.Should().BeEmpty();
        p.RemoveAttributeValue(id).Should().BeNull();
    }
}
