using FluentAssertions;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;

namespace Mechware.UnitTests.Catalog;

public sealed class ProductStaffEditTests
{
    private static Product Draft(bool media = true)
    {
        var p = new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "Drive", "ড্রাইভ", "drive", "s", "s", "<p>d</p>");
        p.AddVariant(Guid.NewGuid(), "DRV-1", null, new Dictionary<string, string>(), new Money(1000, Currency.BDT), null, null, 0.5m, true);
        if (media) p.AddMedia(Guid.NewGuid(), null, "/a.jpg", "front", 100, 100);
        return p;
    }

    [Theory]
    [InlineData("<p>ok <strong>x</strong></p><ul><li>a</li></ul>", 0)]
    [InlineData("<a href=\"https://example.com\">x</a>", 0)]
    [InlineData("<script>alert(1)</script>", 1)]
    [InlineData("<p onclick=\"x()\">a</p>", 1)]
    [InlineData("<a href=\"javascript:alert(1)\">x</a>", 1)]
    [InlineData("<iframe src=\"https://x\"></iframe>", 1)]
    [InlineData("<!-- c --><p>a</p>", 1)]
    public void FindProblems_ReportsWhatTheSanitizerWouldStrip(string html, int minProblems)
    {
        var problems = RichTextSanitizer.FindProblems(html);
        if (minProblems == 0) problems.Should().BeEmpty();
        else problems.Count.Should().BeGreaterThanOrEqualTo(minProblems);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("1000.001")]
    [InlineData("1.2345")]
    public void SetWeight_RefusesZeroNegativeHugeAndTooPrecise(string weight)
    {
        var p = Draft();
        var variant = p.Variants.Single();
        var act = () => p.UpdateVariant(variant.Id, decimal.Parse(weight, System.Globalization.CultureInfo.InvariantCulture), null, null);
        act.Should().Throw<ValidationException>().Which.Errors[0].Field.Should().Be("weightKg");
        variant.WeightKg.Should().Be(0.5m);
    }

    [Fact]
    public void UpdateVariant_ChangesOnlyWhatIsSent_AndRaisesTheUpdateEvent()
    {
        var p = Draft();
        var v = p.Variants.Single();
        p.ClearDomainEvents();
        p.UpdateVariant(v.Id, 2.345m, Money.FromMinorUnits(2_000_00, Currency.BDT), VariantStatus.Inactive);
        (v.WeightKg, v.PriceMinor, v.Status).Should().Be((2.345m, 200_000, VariantStatus.Inactive));
        p.DomainEvents.OfType<ProductUpdatedEvent>().Should().HaveCount(1);

        p.UpdateVariant(v.Id, null, null, null);
        (v.WeightKg, v.PriceMinor, v.Status).Should().Be((2.345m, 200_000, VariantStatus.Inactive));
        var unknown = () => p.UpdateVariant(Guid.NewGuid(), 1m, null, null);
        unknown.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Publish_TakesADraftToActive_RefusesWithoutAnImage_AndRefusesAnActiveOne()
    {
        var p = Draft();
        p.Publish();
        p.Status.Should().Be(ProductStatus.Active);
        var again = () => p.Publish();
        again.Should().Throw<InvalidProductTransitionException>();

        var noImage = Draft(media: false);
        var act = () => noImage.Publish();
        act.Should().Throw<ValidationException>().Which.Errors[0].Field.Should().Be("media");
    }
}
