using FluentAssertions;
using Mechware.Application.Quotes;
using Mechware.Domain.Quotes;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.10, threat model QT6 (ADR-0011 §13): the buyer quote DTOs are built field by field, and these lists pin them, nested records included,
/// so a new field (a cost, a flag, a staff id) cannot reach a buyer without this test changing.
/// </summary>
public sealed class BuyerQuoteDtoTests
{
    [Fact]
    public void BuyerQuoteResponse_HasOnlyTheAllowedFields()
    {
        Names<BuyerQuoteSummaryDto>().Should().BeEquivalentTo("Id", "Number", "RfqId", "VersionNo", "Status", "SentAt", "ValidUntil", "LineCount",
            "GoodsTotalMinor", "GrandTotalMinor", "PaymentTerms", "ShippingMode", "ContactIsMe", "ClosedAt");
        Names<BuyerQuoteVersionDto>().Should().BeEquivalentTo("VersionNo", "Status", "Acceptable", "SentAt", "ValidUntil", "ValidityDays", "PaymentTerms",
            "ShippingMode", "ShipToSiteId", "ShipTo", "ShippingFees", "TermsText", "BuyerNote", "Company", "PreparerName", "ContentHash", "GoodsNetMinor",
            "TaxMinor", "GoodsTotalMinor", "ShippingTotalMinor", "GrandTotalMinor", "AcceptedOrderId", "AcceptedAt", "RejectedAt", "RejectReason",
            "WithdrawnAt", "SupersededAt", "Lines");
        Names<BuyerQuoteLineDto>().Should().BeEquivalentTo("Id", "LineNo", "VariantId", "Sku", "PartNumber", "TitleEn", "TitleBn", "Quantity",
            "UnitPriceMinor", "VatRate", "LineNetMinor", "LineTaxMinor", "LineTotalMinor", "LineNote");
        Names<BuyerQuoteDetailDto>().Should().BeEquivalentTo("Quote", "Versions");
        Names<BuyerQuotePage>().Should().BeEquivalentTo("Items", "Page", "PageSize", "Total");
        Names<BuyerQuoteRefDto>().Should().BeEquivalentTo("CompanyId", "QuoteId", "Number");
        // The frozen parts of the send, as the buyer gets them.
        Names<QuoteShipTo>().Should().BeEquivalentTo("SiteName", "RecipientName", "Phone", "Line1", "Line2", "Upazila", "District", "Division", "PostalCode");
        Names<QuoteCompanySnapshot>().Should().BeEquivalentTo("Name", "Bin", "BillingAddress");
        Names<QuoteShippingFee>().Should().BeEquivalentTo("SellerId", "ShippingMinor");
        Names<QuoteAcceptPreviewDto>().Should().BeEquivalentTo("QuoteId", "VersionNo", "Lines", "SubtotalMinor", "TaxMinor", "ShippingMinor", "GrandTotalMinor",
            "VatRateChanged", "LowerPriceNow", "StockShort");
        Names<QuoteAcceptPreviewLineDto>().Should().BeEquivalentTo("QuoteLineId", "VariantId", "Sku", "Title", "Quantity", "UnitPriceMinor", "VatRate", "NetMinor",
            "TaxMinor", "TotalMinor", "VatRateChanged", "LowerPriceNow", "StockShort");
    }

    [Fact]
    public void BuyerQuoteTypes_NameNoStaffOnlyFigure()
    {
        Type[] types = [typeof(BuyerQuoteSummaryDto), typeof(BuyerQuoteVersionDto), typeof(BuyerQuoteLineDto), typeof(QuoteAcceptPreviewDto),
            typeof(QuoteAcceptPreviewLineDto), typeof(QuoteShipTo), typeof(QuoteCompanySnapshot), typeof(QuoteShippingFee)];
        string[] staffOnly = ["Cost", "Floor", "Margin", "Flag", "Reference", "Internal", "Approv", "Submit", "Contributor", "CreatedBy", "SentBy", "WithdrawnBy",
            "Public", "Discount", "Email", "Reason"];
        foreach (var name in types.SelectMany(t => t.GetProperties().Select(p => p.Name)).Where(n => n != "RejectReason"))
            staffOnly.Should().NotContain(s => name.Contains(s, StringComparison.OrdinalIgnoreCase), name);
    }

    private static IEnumerable<string> Names<T>() => typeof(T).GetProperties().Select(p => p.Name);
}
