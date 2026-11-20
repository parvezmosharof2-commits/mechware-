using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.9, the order side of an accepted quote (ADR-0011 §8.3, §11; threat model QT2): a quote line is priced at the quoted net price with
/// the VAT rate given, takes no discount, and names its quote line exactly when its price source is Quote; an order of a quote is a
/// company order whose every line is a quote line, linked to the quote and the version (both or neither).
/// </summary>
public class QuoteOrderLineTests
{
    private static readonly DateTime Now = new(2026, 11, 20, 6, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Seller = Guid.NewGuid();

    private static OrderLineDraft QuoteLine(long unit = 150_000, int qty = 2, decimal vat = 0.10m, long discount = 0, Guid? quoteLine = null)
    {
        var variant = Guid.NewGuid();
        return new OrderLineDraft(Guid.NewGuid(), variant, Seller, "BRG-6204", "Ball bearing 6204", qty, unit, vat, discount,
            new PriceResolution(variant, unit, PriceSource.Quote, PriceBasis.NetOfVat, null, null, null, null, 160_000), quoteLine ?? Guid.NewGuid());
    }

    private static PricedOrder Price(params OrderLineDraft[] lines) => OrderPricing.Price(lines, [new SellerTerms(Seller, 0, 0.08m)], TaxMode.Exclusive);

    [Fact]
    public void AQuoteLine_IsPricedAtItsNetPriceWithTheGivenVat_AndKeepsItsQuoteLine()
    {
        var draft = QuoteLine();
        var line = Price(draft).SubOrders.Single().Lines.Single();
        (line.NetMinor, line.TaxMinor, line.TotalMinor, line.QuoteLineId).Should().Be((300_000, 30_000, 330_000, draft.QuoteLineId));
    }

    [Fact]
    public void AQuoteLine_NeverGetsAPromotionOrCoupon_AtTheDomainLevel()
    {
        FluentActions.Invoking(() => Price(QuoteLine(discount: 1))).Should().Throw<ValidationException>("a quote price takes no discount");
    }

    [Fact]
    public void AQuotePriceWithoutItsLine_OrALineOnAnotherPrice_IsRefused()
    {
        var withoutLine = QuoteLine() with { QuoteLineId = null };
        FluentActions.Invoking(() => Price(withoutLine)).Should().Throw<ValidationException>();

        var listed = QuoteLine();
        var listPrice = listed with { Price = listed.Price! with { Source = PriceSource.List, Basis = PriceBasis.NetOfVat } };
        FluentActions.Invoking(() => Price(listPrice)).Should().Throw<ValidationException>("only a quote price names a quote line");
    }

    [Fact]
    public void AnOrderOfAQuote_IsACompanyOrderOfQuoteLines_LinkedToTheQuoteAndTheVersion()
    {
        var (company, quote, version) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var priced = Price(QuoteLine(), QuoteLine());
        Order Place(PricedOrder p, Guid? companyId, Guid? q, Guid? v) =>
            Order.Place(Guid.NewGuid(), OrderNumber.Generate(Now), Guid.NewGuid(), companyId, p, TaxMode.Exclusive, "{}", OrderStatus.PendingPayment, Now,
                paymentDueAtUtc: Now.AddMinutes(30), quoteId: q, quoteVersionId: v);

        var order = Place(priced, company, quote, version);
        (order.QuoteId, order.QuoteVersionId).Should().Be((quote, version));
        order.SubOrders.SelectMany(s => s.Items).Select(i => i.QuoteLineId)
            .Should().Equal(priced.SubOrders.SelectMany(s => s.Lines).Select(l => l.QuoteLineId));

        FluentActions.Invoking(() => Place(priced, company, quote, null)).Should().Throw<ValidationException>("both or neither");
        FluentActions.Invoking(() => Place(priced, company, null, version)).Should().Throw<ValidationException>("both or neither");
        FluentActions.Invoking(() => Place(priced, null, quote, version)).Should().Throw<ValidationException>("a quote is a company's");
        FluentActions.Invoking(() => Place(priced, company, null, null)).Should().Throw<ValidationException>("quote lines belong to a quote order");

        var cart = OrderPricing.Price([new OrderLineDraft(Guid.NewGuid(), Guid.NewGuid(), Seller, "S", "T", 1, 1_000, 0.05m, 0)],
            [new SellerTerms(Seller, 0, 0.08m)], TaxMode.Exclusive);
        FluentActions.Invoking(() => Place(cart, company, quote, version)).Should().Throw<ValidationException>("every line of a quote order is a quote line");
        Place(cart, company, null, null).QuoteId.Should().BeNull();
    }
}
