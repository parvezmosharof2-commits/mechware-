using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.7 domain half of the draft editor (ADR-0011 §3, §5, §6; threat model QT6): the contact rule, contributors, the conflict-of-interest
/// refusal on every edit, and the ship-to copy carried by fixed delivery.
/// </summary>
public class QuoteDraftEditorTests
{
    private static readonly Guid NewContact = Guid.Parse("00000000-0000-0000-0000-0000000000a7");

    [Fact]
    public void TheContact_ChangesOnlyWhileNoVersionWaitsIsSentOrIsAccepted()
    {
        var draft = Drafted();
        draft.ChangeContact(NewContact, Staff(Editor), Now.AddMinutes(1));
        draft.ContactUserId.Should().Be(NewContact);
        draft.ChangeContact(NewContact, Staff(Editor), Now.AddMinutes(2));   // the same contact again: nothing to refuse

        foreach (var status in new[] { QuoteVersionStatus.PendingApproval, QuoteVersionStatus.Sent, QuoteVersionStatus.Accepted })
        {
            var quote = WithVersion1In(status);
            var hash = quote.Version(1).ContentHash;
            FluentActions.Invoking(() => quote.ChangeContact(NewContact, Staff(Editor), Now.AddHours(3)))
                .Should().Throw<ConflictException>(status.ToString()).Which.Code.Should().Be("QUOTE_CONTACT_LOCKED");
            quote.ContactUserId.Should().Be(Contact, "a refusal changes nothing");
            QuoteContentHash.Matches(quote, quote.Version(1), hash).Should().BeTrue("the hash the approver or buyer holds still holds");
        }

        // Once the sent version is withdrawn (or rejected), nothing live holds the old contact; the next version is sent to the new one.
        foreach (var status in new[] { QuoteVersionStatus.Withdrawn, QuoteVersionStatus.Rejected })
        {
            var quote = WithVersion1In(status);
            quote.Revise(Guid.NewGuid(), Staff(Editor), false, Now.AddHours(2));
            quote.ChangeContact(NewContact, Staff(Editor), Now.AddHours(3));
            quote.ContactUserId.Should().Be(NewContact);
        }

        // A version superseded by a sent one: the sent one holds the contact.
        FluentActions.Invoking(() => WithVersion1In(QuoteVersionStatus.Superseded).ChangeContact(NewContact, Staff(Editor), Now.AddHours(3)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CONTACT_LOCKED");
    }

    [Fact]
    public void ChangingTheContact_IsRefusedToAStaffMemberOfTheCompany_AndNeedsAContact_AndAnOpenQuote()
    {
        var quote = Drafted();
        FluentActions.Invoking(() => quote.ChangeContact(NewContact, new QuoteActor(Editor, [CompanyB, CompanyA]), Now))
            .Should().Throw<ForbiddenException>().Which.Code.Should().Be("QUOTE_CONFLICT_OF_INTEREST");
        FluentActions.Invoking(() => quote.ChangeContact(Guid.Empty, Staff(Editor), Now)).Should().Throw<ValidationException>();
        quote.ContactUserId.Should().Be(Contact);

        quote.Close(Editor, "Duplicate request", Now);
        FluentActions.Invoking(() => quote.ChangeContact(NewContact, Staff(Editor), Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CLOSED");
    }

    [Fact]
    public void EveryEditor_IsRecordedOnce_AsAContributorOfTheVersion_AndMayNotApproveIt()
    {
        var quote = Drafted();   // Author created it
        quote.EditDraft(1, Content(Line(V1, price: 99_000)), Staff(Editor), Options, Now.AddMinutes(1));
        quote.EditDraft(1, Content(Line(V1, price: 98_000)), Staff(Editor), Options, Now.AddMinutes(2));
        quote.EditDraft(1, Content(Line(V1, price: 97_000)), Staff(Approver), Options, Now.AddMinutes(3));
        quote.Version(1).Contributors.Select(c => c.UserId).Should().Equal(Author, Editor, Approver);

        quote.Send(1, Issue(quote, overAuthority: true), Staff(Sender), false, Options, Now.AddMinutes(4)).Should().Be(QuoteVersionStatus.PendingApproval);
        FluentActions.Invoking(() => ApproveAs(quote, 1, Now.AddMinutes(5), approver: Approver))
            .Should().Throw<ConflictException>("an editor is a contributor").Which.Code.Should().Be("QUOTE_FOUR_EYES");
    }

    [Fact]
    public void AStaffMemberOfTheCompany_CannotEditItsDraft_AndTheDraftIsUnchanged()
    {
        var quote = Drafted();
        var before = quote.Version(1).Lines.Select(l => (l.VariantId, l.UnitPriceMinor)).ToList();
        FluentActions.Invoking(() => quote.EditDraft(1, Content(Line(V1, price: 1)), new QuoteActor(Editor, [CompanyA]), Options, Now))
            .Should().Throw<ForbiddenException>().Which.Code.Should().Be("QUOTE_CONFLICT_OF_INTEREST");
        quote.Version(1).Lines.Select(l => (l.VariantId, l.UnitPriceMinor)).Should().Equal(before);
        quote.Version(1).Contributors.Select(c => c.UserId).Should().Equal(Author);
    }

    [Fact]
    public void AFixedDeliveryDraftWithoutItsAddressCopy_CannotBeSent()
    {
        var quote = Drafted();
        quote.EditDraft(1, Content(Line(V1)) with
        {
            ShippingMode = QuoteShippingMode.Fixed,
            ShipToSiteId = Site,
            ShippingFees = [new(Seller1, 5_000)],
            ShipTo = ShipTo,
        }, Staff(Author), Options, Now);
        // As a draft saved before D4.7 would load: a site but no copy of its address.
        typeof(QuoteVersion).GetProperty(nameof(QuoteVersion.ShipTo))!.SetValue(quote.Version(1), null);

        FluentActions.Invoking(() => quote.Send(1, Issue(quote), Staff(Sender), false, Options, Now))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainSingle(e => e.Field == "shipToSiteId");
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Draft);
    }
}
