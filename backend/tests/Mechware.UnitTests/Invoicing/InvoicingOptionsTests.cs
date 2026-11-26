using FluentAssertions;
using Mechware.Domain.Invoicing;
using Microsoft.Extensions.Configuration;

namespace Mechware.UnitTests.Invoicing;

/// <summary>ADR-0012 §3, threat model §১৪ IT7: the <c>Invoicing</c> section and its start-up rules.</summary>
public class InvoicingOptionsTests
{
    private static InvoicingOptions WithMode(InvoicingMode? mode, bool facts) =>
        facts ? InvoiceFixtures.Live with { Mode = mode } : new InvoicingOptions { Mode = mode };

    /// <summary>The whole matrix: environment × mode × issuer facts complete.</summary>
    [Theory]
    [InlineData(false, null, false, true)]   // development, nothing written: Off
    [InlineData(false, InvoicingMode.Off, false, true)]
    [InlineData(false, InvoicingMode.Specimen, false, true)] // a specimen prints "‹missing›"
    [InlineData(false, InvoicingMode.Live, false, false)]
    [InlineData(false, InvoicingMode.Live, true, true)]
    [InlineData(true, null, false, false)]   // Production has no default mode
    [InlineData(true, null, true, false)]
    [InlineData(true, InvoicingMode.Off, false, true)]  // an explicit owner choice (§18 O1 b)
    [InlineData(true, InvoicingMode.Specimen, false, false)]
    [InlineData(true, InvoicingMode.Specimen, true, false)]
    [InlineData(true, InvoicingMode.Live, false, false)]
    [InlineData(true, InvoicingMode.Live, true, true)]
    public void TheStartupRules_FollowTheMatrix(bool production, InvoicingMode? mode, bool facts, bool starts)
    {
        var act = () => WithMode(mode, facts).Validated(production);
        if (starts) act.Should().NotThrow();
        else act.Should().Throw<InvalidOperationException>().WithMessage("Invalid invoicing options: Invoicing:*");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LiveModeWithoutTheIssuerFacts_DoesNotStart(bool production)
    {
        foreach (var (name, options) in new (string, InvoicingOptions)[]
                 {
                     ("IssuerSellerId", InvoiceFixtures.Live with { IssuerSellerId = null }),
                     ("IssuerSellerId", InvoiceFixtures.Live with { IssuerSellerId = Guid.Empty }),
                     ("LegalNameEn", InvoiceFixtures.Live with { LegalNameEn = " " }),
                     ("LegalNameBn", InvoiceFixtures.Live with { LegalNameBn = null }),
                     ("Bin", InvoiceFixtures.Live with { Bin = null }),
                     ("Bin", InvoiceFixtures.Live with { Bin = "123456789012" }),
                     ("RegisteredAddressEn", InvoiceFixtures.Live with { RegisteredAddressEn = "" }),
                     ("RegisteredAddressBn", InvoiceFixtures.Live with { RegisteredAddressBn = null }),
                     ("ResponsiblePersonName", InvoiceFixtures.Live with { ResponsiblePersonName = null }),
                     ("ResponsiblePersonDesignation", InvoiceFixtures.Live with { ResponsiblePersonDesignation = "\t" }),
                 })
        {
            options.Invoking(o => o.Validated(production)).Should().Throw<InvalidOperationException>().WithMessage($"*Invoicing:{name} *");
        }

        // Optional on the form: TIN, trade licence, premises address (defaults to the registered one).
        (InvoiceFixtures.Live with { Tin = null, TradeLicence = null, IssuingPremisesAddressEn = null }).Validated(production).Mode.Should().Be(InvoicingMode.Live);
    }

    [Fact]
    public void SpecimenMode_InProduction_DoesNotStart()
    {
        var act = () => (InvoiceFixtures.Live with { Mode = InvoicingMode.Specimen }).Validated(isProduction: true);
        act.Should().Throw<InvalidOperationException>().WithMessage("*Specimen is not allowed in Production*");
        (InvoiceFixtures.Live with { Mode = InvoicingMode.Specimen }).Validated(isProduction: false).EffectiveMode.Should().Be(InvoicingMode.Specimen);
    }

    [Fact]
    public void Production_WithoutAnExplicitMode_DoesNotStart_AndElsewhereTheDefaultIsOff()
    {
        new InvoicingOptions().Invoking(o => o.Validated(isProduction: true)).Should().Throw<InvalidOperationException>().WithMessage("*there is no default*");
        new InvoicingOptions().Validated(isProduction: false).EffectiveMode.Should().Be(InvoicingMode.Off);
    }

    /// <summary>A BIN is 13 ASCII digits as best known (ADR-0012 §19 Q8: the consultant confirms the format).</summary>
    [Theory]
    [InlineData("0001234567890", true)]
    [InlineData("000123456-7890", true)]
    [InlineData("000000000-0101", true)]
    [InlineData("00012345-67890", false)]     // dash in the wrong place
    [InlineData("0001234567-890", false)]
    [InlineData("000123456-78901", false)]    // 14 digits
    [InlineData("000123456--890", false)]
    [InlineData("000123456-789A", false)]
    [InlineData("০০০১২৩৪৫৬-৭৮৯০", false)]    // Bangla digits with the dash
    [InlineData("-000123456789", false)]
    [InlineData("000123456–7890", false)]     // en dash
    [InlineData("1234567890123", true)]
    [InlineData("123456789012", false)]
    [InlineData("12345678901234", false)]
    [InlineData("000123456789A", false)]
    [InlineData("000123 567890", false)]
    [InlineData("-001234567890", false)]
    [InlineData("০০০১২৩৪৫৬৭৮৯০", false)] // Bangla digits are digits to char.IsDigit, but not a BIN
    [InlineData("０００１２３４５６７８９０", false)] // full-width
    [InlineData("", false)]
    [InlineData(null, false)]
    public void ABin_IsThirteenAsciiDigits_PlainOrDashed(string? bin, bool valid)
    {
        TaxIdFormat.IsBin(bin).Should().Be(valid);
        var options = new InvoicingOptions { Mode = InvoicingMode.Off, Bin = bin };
        options.Problems(isProduction: false).Any(p => p.StartsWith("Invoicing:Bin ", StringComparison.Ordinal)).Should().Be(!valid && !string.IsNullOrEmpty(bin));
    }

    [Fact]
    public void TheOtherValues_AreChecked_InEveryMode()
    {
        string[] Problems(InvoicingOptions o) => [.. o.Problems(isProduction: false)];
        Problems(new() { NumberPrefix = "SPEC" }).Should().ContainMatch("Invoicing:NumberPrefix *");
        Problems(new() { NumberPrefix = "mw" }).Should().ContainMatch("Invoicing:NumberPrefix *");
        Problems(new() { NumberPrefix = "M" }).Should().ContainMatch("Invoicing:NumberPrefix *");
        Problems(new() { Tin = "12345678901" }).Should().ContainMatch("Invoicing:Tin *");
        Problems(new() { CreditNoteMaxAgeDays = 0 }).Should().ContainMatch("Invoicing:CreditNoteMaxAgeDays *");
        Problems(new() { ShippingVatRate = 0.6m }).Should().ContainMatch("Invoicing:ShippingVatRate *");
        Problems(new() { ShippingVatRate = 0.12345m }).Should().ContainMatch("Invoicing:ShippingVatRate *");
        Problems(new() { ShippingVatRate = -0.01m }).Should().ContainMatch("Invoicing:ShippingVatRate *");
        Problems(new() { Mode = (InvoicingMode)7 }).Should().ContainMatch("Invoicing:Mode *");
        Problems(new() { ShippingVatRate = 0.15m, NumberPrefix = "MECHWR", Tin = "123456789012" }).Should().BeEmpty();

        var all = new InvoicingOptions { Mode = InvoicingMode.Live, NumberPrefix = "x" }.Problems(isProduction: true);
        all.Should().HaveCount(9, "every missing fact and every bad value is named at once");
    }

    [Fact]
    public void TheSection_BindsFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Invoicing:Mode"] = "Live",
            ["Invoicing:IssuerSellerId"] = InvoiceFixtures.Seller.ToString(),
            ["Invoicing:LegalNameEn"] = "MECHWARE",
            ["Invoicing:Bin"] = "0001234567890",
            ["Invoicing:ShippingVatRate"] = "0.15",
            ["Invoicing:ConsultantChecked"] = "true",
        }).Build();
        var options = config.GetSection(InvoicingOptions.Section).Get<InvoicingOptions>()!;
        (options.Mode, options.IssuerSellerId, options.Bin, options.ShippingVatRate, options.ConsultantChecked, options.NumberPrefix, options.CreditNoteMaxAgeDays)
            .Should().Be((InvoicingMode.Live, InvoiceFixtures.Seller, "0001234567890", 0.15m, true, "MW", 180));
        options.MissingIssuerFacts().Should().Equal("LegalNameBn", "RegisteredAddressEn", "RegisteredAddressBn", "ResponsiblePersonName", "ResponsiblePersonDesignation");
    }

    [Theory]
    [InlineData("000123456-7890", "0001234567890", "000123456-7890")]
    [InlineData("0001234567890", "0001234567890", "000123456-7890")]
    [InlineData("000000000-0101", "0000000000101", "000000000-0101")]
    public void ABin_IsStoredAsDigits_AndPrintedDashed(string written, string stored, string printed)
    {
        TaxIdFormat.NormalizeBin(written).Should().Be(stored);
        TaxIdFormat.PrintBin(stored).Should().Be(printed);
        TaxIdFormat.PrintBin(printed).Should().Be(printed);
        TaxIdFormat.PrintBin("not a bin").Should().Be("not a bin");
        TaxIdFormat.NormalizeBin("12-34").Should().BeNull();
    }

    [Fact]
    public void ADashedIssuerBin_IsSnapshottedAsDigits()
    {
        var (invoice, _, _, _) = InvoiceFixtures.CompanyInvoice(InvoiceFixtures.Live with { Bin = "000123456-7890" });
        invoice.Seller.Bin.Should().Be("0001234567890");
    }

    [Fact]
    public void ACompany_RegisteredWithADashedBin_IsStoredAsDigits_AndTheBuyerBinIsMasked()
    {
        var c = Mechware.Domain.Companies.Company.Register(Guid.NewGuid(), "Dash Ltd", "TL-1", "000123456-7890", null, null);
        c.Bin.Should().Be("0001234567890");
        Action bad = () => Mechware.Domain.Companies.Company.Register(Guid.NewGuid(), "Dash Ltd", "TL-1", "00012345-67890", null, null);
        bad.Should().Throw<Exception>();
        Mechware.Infrastructure.Invoicing.InvoiceReadService.MaskBin(c.Bin).Should().Be("*********7890");
    }

    [Fact]
    public void TheBankAndSignatorySections_BindFromConfiguration()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Invoicing:Bank:AccountName"] = "Test Trading Ltd",
            ["Invoicing:Bank:AccountNumber"] = "1234567890123",
            ["Invoicing:Bank:BankName"] = "Test Bank",
            ["Invoicing:Bank:Branch"] = "Gulshan",
            ["Invoicing:Bank:RoutingNumber"] = "123456789",
            ["Invoicing:Signatory:Name"] = "Test Signatory",
            ["Invoicing:Signatory:Designation"] = "Director",
            ["Invoicing:Bin"] = "000123456-7890"
        }).Build();
        var o = config.GetSection(InvoicingOptions.Section).Get<InvoicingOptions>()!;
        o.Bank.Should().Be(new InvoicingBankOptions { AccountName = "Test Trading Ltd", AccountNumber = "1234567890123", BankName = "Test Bank", Branch = "Gulshan", RoutingNumber = "123456789" });
        o.Signatory.Should().Be(new InvoicingSignatoryOptions { Name = "Test Signatory", Designation = "Director" });
        o.Problems(isProduction: false).Should().BeEmpty("a dashed BIN is accepted");
    }
}
