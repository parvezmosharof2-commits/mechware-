using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.8 domain half of the send (ADR-0011 §4, §6, §8.3; threat model QT16): a fixed delivery's address is taken from the site as it is
/// at the send, not as it was when the draft was saved, and a site that is gone refuses the send.
/// </summary>
public class QuoteSendShipToTests
{
    private static Quote FixedDraft()
    {
        var quote = Drafted();
        quote.EditDraft(1, Content(Line(V1)) with
        {
            ShippingMode = QuoteShippingMode.Fixed,
            ShipToSiteId = Site,
            ShippingFees = [new(Seller1, 5_000)],
            ShipTo = ShipTo,
        }, Staff(Author), Options, Now);
        return quote;
    }

    [Fact]
    public void ASend_TakesTheSiteAddressOfThatMoment_AndHashesIt()
    {
        var quote = FixedDraft();
        var moved = ShipTo with { Line1 = "House 7, Road 3\u0007", RecipientName = "Selina Akter" };

        quote.Send(1, Issue(quote) with { ShipTo = moved }, Staff(Sender), false, Options, Now).Should().Be(QuoteVersionStatus.Sent);

        var v = quote.Version(1);
        v.ShipTo.Should().Be(moved with { Line1 = "House 7, Road 3" }, "the copy is refreshed at the send and cleaned as plain text (QT17)");
        v.ShipToSiteId.Should().Be(Site);
        v.ContentHash.Should().Be(QuoteContentHash.Compute(quote, v), "the hash covers the address that was sent");
    }

    [Fact]
    public void ASend_OfAFixedDeliveryWhoseSiteIsGone_IsRefused_AndChangesNothing()
    {
        var quote = FixedDraft();

        FluentActions.Invoking(() => quote.Send(1, Issue(quote) with { ShipTo = null }, Staff(Sender), false, Options, Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_DELIVERY_SITE_GONE");

        var v = quote.Version(1);
        (v.Status, v.ShipTo, v.ContentHash, v.SubmittedBy).Should().Be((QuoteVersionStatus.Draft, ShipTo, (string?)null, (Guid?)null));
        quote.DomainEvents.Should().BeEmpty();
    }

    [Fact]
    public void ASend_OfCalculatedDelivery_TakesNoAddress()
    {
        var quote = Drafted();

        FluentActions.Invoking(() => quote.Send(1, Issue(quote) with { ShipTo = ShipTo }, Staff(Sender), false, Options, Now))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainSingle(e => e.Field == "shipTo");
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Draft);

        quote.Send(1, Issue(quote), Staff(Sender), false, Options, Now).Should().Be(QuoteVersionStatus.Sent);
        quote.Version(1).ShipTo.Should().BeNull();
    }
}
