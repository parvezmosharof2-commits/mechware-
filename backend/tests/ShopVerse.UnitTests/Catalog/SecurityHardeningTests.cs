using FluentAssertions;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.ValueObjects;
using ShopVerse.Domain.Pricing;

namespace ShopVerse.UnitTests.Catalog;

/// <summary>Regression tests for the Phase 4 security / correctness audit (R1 + R3).</summary>
public class SecurityHardeningTests
{
    private static Money Tk(decimal v) => new(v, Currency.BDT);
    private static readonly DateTime T0 = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("JaVaScRiPt:alert(1)")]
    [InlineData("data:text/html;base64,PHNjcmlwdD4=")]
    [InlineData("//evil.example/x.png")]
    [InlineData("http://insecure.example/x.png")]
    [InlineData("ftp://x/y")]
    [InlineData("/ok path")]
    [InlineData("/ok\\path")]
    [InlineData("https://")]
    [InlineData("")]
    public void SafeUrl_RejectsDangerousOrMalformedUrls(string url) => SafeUrl.IsSafe(url).Should().BeFalse();

    [Theory]
    [InlineData("https://cdn.mechware.com.bd/p/a.jpg")]
    [InlineData("/seed-media/products/a/1.svg")]
    public void SafeUrl_AcceptsHttpsAndSiteRelative(string url) => SafeUrl.IsSafe(url).Should().BeTrue();

    [Fact]
    public void Entities_RefuseUnsafeUrls()
    {
        var product = new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "T", "টি", "t", "s", "s", "d");
        var media = () => product.AddMedia(Guid.NewGuid(), null, "javascript:alert(1)", "alt", 10, 10);
        media.Should().Throw<ValidationException>();
        var blur = () => product.AddMedia(Guid.NewGuid(), null, "/a.jpg", "alt", 10, 10, "data:text/html;base64,AAAA");
        blur.Should().Throw<ValidationException>();
        var datasheet = () => product.SetContent("[]", "{}", "javascript:alert(1)", null);
        datasheet.Should().Throw<ValidationException>();
        var brand = () => new Brand(Guid.NewGuid(), "X", "x", "javascript:alert(1)", false);
        brand.Should().Throw<ValidationException>();
    }

    [Fact]
    public void RichText_IsSanitized_OnConstructionAndUpdate()
    {
        const string evil = "<p onclick=\"x()\">Hi</p><script>alert(1)</script><img src=x onerror=alert(1)><a href=\"javascript:alert(1)\">go</a><iframe src=\"//evil\"></iframe><h3>Specs</h3><ul><li>24 V</li></ul>";
        var p = new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "T", "টি", "t", "s", "s", evil);

        p.DescriptionHtml.Should().NotContainAny("script", "onclick", "onerror", "javascript:", "iframe", "<img");
        p.DescriptionHtml.Should().Contain("<h3>Specs</h3>").And.Contain("<li>24 V</li>").And.Contain("<p>Hi</p>");

        p.SetDescription("s", "s", "<b>ok</b><script>x</script>");
        p.DescriptionHtml.Should().Be("<b>ok</b>");
    }

    [Fact]
    public void Sale_NeverRaisesThePrice_WhenRegularPriceWasLoweredAfterScheduling()
    {
        var input = new PricingInput(Tk(500m), null, Tk(750m), T0, T0.AddDays(1), 0.05m);
        var quote = PricingEngine.Quote(input, T0.AddHours(1), TaxMode.Inclusive);

        quote.SaleActive.Should().BeFalse();
        quote.Display.Should().Be(Tk(500m));
        quote.DiscountPercent.Should().Be(0);
    }

    [Fact]
    public void ChangePrice_RejectsRaisingPriceToOrAboveScheduledSale_AndSaleRequiresUtc()
    {
        var p = new Product(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), null, "T", "টি", "t", "s", "s", "d");
        var v = p.AddVariant(Guid.NewGuid(), "S1", null, new Dictionary<string, string>(), Tk(1000), null, null, 1m, true);
        v.ScheduleSale(Tk(800), T0, T0.AddDays(1));

        var lower = () => v.ChangePrice(Tk(700), null);
        lower.Should().Throw<ValidationException>("a regular price at or below the sale price makes the sale meaningless");
        v.ChangePrice(Tk(900), null);

        var local = () => v.ScheduleSale(Tk(600), DateTime.SpecifyKind(T0, DateTimeKind.Unspecified), T0.AddDays(1));
        local.Should().Throw<ValidationException>();
    }
}
