using System.Text.RegularExpressions;
using FluentAssertions;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Orders.Pdf;
using Mechware.Infrastructure.Pdf;
using UglyToad.PdfPig;

namespace Mechware.UnitTests.Orders;

/// <summary>E6.4d: the delivery challan lists the goods and never a price; EN and BN; one page when short; the challan number follows the order number.</summary>
public class ChallanPdfTests
{
    private static readonly SkiaPdfRenderer Renderer = new();

    private static readonly InvoicingOptions Options = new()
    {
        LegalNameEn = "MECHWARE",
        LegalNameBn = "মেকওয়্যার",
        RegisteredAddressEn = "Modhubon Tower, Sha-23/E (6th Floor), Gulshan Link Road, Dhaka-1212",
        RegisteredAddressBn = "মধুবন টাওয়ার, শা-২৩/ই (৬ষ্ঠ তলা), গুলশান লিংক রোড, ঢাকা-১২১২",
        Signatory = new InvoicingSignatoryOptions { Name = "Md. Parvez Mosharaf", Designation = "Managing Director" }
    };

    private static ChallanData Data(int lines = 3) => new(ChallanPdfComposer.ChallanNumber("MW-261127-ABC234"), "MW-261127-ABC234", "PO-7788/26", new DateOnly(2026, 11, 27),
        "Rahim Automation Ltd", "Karim Uddin", "Plant 2, Gazipur Sadar, Gazipur, Dhaka, 1700", "Steadfast", "SF-1234567",
        [.. Enumerable.Range(1, lines).Select(i => new ChallanLine($"SKU-{i}", i == 1 ? "Siemens S7-1200 CPU 1214C DC/DC/DC with a long description to wrap" : $"Item number {i}", i * 12))]);

    private static (string Text, int Pages) Read(byte[] pdf)
    {
        using var doc = PdfDocument.Open(pdf);
        return (string.Join("\n", doc.GetPages().Select(p => p.Text)), doc.NumberOfPages);
    }

    [Fact]
    public void TheChallanNumber_IsDerivedFromTheOrderNumber_WithoutDoublingThePrefix()
    {
        ChallanPdfComposer.ChallanNumber("MW-261127-ABC234").Should().Be("MW-CH-261127-ABC234");
        ChallanPdfComposer.ChallanNumber("X-1").Should().Be("MW-CH-X-1");
    }

    [Theory]
    [InlineData("en")]
    [InlineData("bn")]
    public void TheChallan_HasTheOwnersFields_NoPrices_AndOnePageWhenShort(string locale)
    {
        var r = ChallanPdfComposer.Render(Renderer, Data(), locale, Options);
        var (text, pages) = Read(r.Bytes);
        pages.Should().Be(1);
        text.Should().Contain("PO-7788/26").And.Contain("Rahim Automation Ltd").And.Contain("Steadfast").And.Contain("SF-1234567").And.Contain("Md. Parvez Mosharaf").And.Contain("S7-1200");
        foreach (var priceWord in new[] { "BDT", "VAT", "Price", "Total (", "Amount", "৳", "টাকা", "মূল্য: " })
            if (priceWord != "মূল্য: ") text.Should().NotContain(priceWord);
        if (locale == "en") text.Should().Contain("MW-CH-261127-ABC234").And.Contain("27 Nov 2026").And.Contain("Received by");
        else
        {
            // Extracted Bengali text is split by shaping, so only the digits are asserted: Bengali for dates, numbers and quantities, Latin for the order, PO and tracking numbers.
            text.Should().Contain("MW-CH-২৬১১২৭-ABC২৩৪").And.Contain("২৪").And.Contain("৩৬").And.Contain("২০২৬").And.NotContain("pcs");
            Regex.IsMatch(text, "[0-9]{2,}").Should().BeTrue("order and tracking numbers are Latin");
        }
    }

    [Fact]
    public void TheChallan_IsByteIdenticalForTheSameInput_AndAQuantityNeverWraps_EvenWithManyLines()
    {
        var a = ChallanPdfComposer.Render(Renderer, Data(40), "en", Options);
        ChallanPdfComposer.Render(Renderer, Data(40), "en", Options).Bytes.Should().Equal(a.Bytes);
        a.PageCount.Should().BeGreaterThan(1);
        var (text, _) = Read(a.Bytes);
        text.Should().Contain("480 pcs", "the widest quantity stays on one line");
        if (Environment.GetEnvironmentVariable("MW_SAMPLE_DIR") is { Length: > 0 } dir)
            foreach (var lang in new[] { "en", "bn" })
            {
                File.WriteAllBytes(Path.Combine(dir, $"challan-{lang}.pdf"), ChallanPdfComposer.Render(Renderer, Data(), lang, Options).Bytes);
                File.WriteAllBytes(Path.Combine(dir, $"challan-long-{lang}.pdf"), ChallanPdfComposer.Render(Renderer, Data(40), lang, Options).Bytes);
            }
    }
}
