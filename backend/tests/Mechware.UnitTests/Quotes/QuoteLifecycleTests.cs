using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>D4.2: the version lifecycle of ADR-0011 §3 (transition table, immutability, supersede, revise, close, accept).</summary>
public class QuoteLifecycleTests
{
    private static readonly QuoteVersionStatus[] AllStatuses = Enum.GetValues<QuoteVersionStatus>();

    // ---- §3 transition table ------------------------------------------------------------------------------------------

    private static readonly HashSet<(QuoteVersionStatus, QuoteVersionStatus)> Section3 =
    [
        (QuoteVersionStatus.Draft, QuoteVersionStatus.Sent), (QuoteVersionStatus.Draft, QuoteVersionStatus.PendingApproval),
        (QuoteVersionStatus.PendingApproval, QuoteVersionStatus.Sent), (QuoteVersionStatus.PendingApproval, QuoteVersionStatus.Draft),
        (QuoteVersionStatus.Sent, QuoteVersionStatus.Accepted), (QuoteVersionStatus.Sent, QuoteVersionStatus.Rejected),
        (QuoteVersionStatus.Sent, QuoteVersionStatus.Withdrawn), (QuoteVersionStatus.Sent, QuoteVersionStatus.Superseded),
        (QuoteVersionStatus.Accepted, QuoteVersionStatus.Superseded),
    ];

    [Fact]
    public void TheTransitionTable_IsExactlySection3_AndEveryOtherPairIsRefused()
    {
        foreach (var from in AllStatuses)
            foreach (var to in AllStatuses)
                QuoteVersion.CanMove(from, to).Should().Be(Section3.Contains((from, to)), $"{from} → {to}");
    }

    /// <summary>Each command, the one status §3 allows it from (the Accepted version's order has not lapsed) and where it moves version 1.</summary>
    private static readonly (string Name, QuoteVersionStatus From, QuoteVersionStatus To, Action<Quote> Run)[] Commands =
    [
        ("send", QuoteVersionStatus.Draft, QuoteVersionStatus.Sent, q => q.Send(1, Issue(q), Staff(Sender), false, Options, Now.AddDays(1))),
        ("send with flags", QuoteVersionStatus.Draft, QuoteVersionStatus.PendingApproval,
            q => q.Send(1, Issue(q, overAuthority: true), Staff(Sender), false, Options, Now.AddDays(1))),
        ("approve", QuoteVersionStatus.PendingApproval, QuoteVersionStatus.Sent, q => ApproveAs(q, 1, Now.AddDays(1))),
        ("reject approval", QuoteVersionStatus.PendingApproval, QuoteVersionStatus.Draft, q => q.RejectApproval(1, Approver, "Price too low", Now.AddDays(1))),
        ("recall", QuoteVersionStatus.PendingApproval, QuoteVersionStatus.Draft, q => q.Recall(1, Author, Now.AddDays(1))),
        ("accept", QuoteVersionStatus.Sent, QuoteVersionStatus.Accepted, q => q.Accept(1, HashOf(q), Guid.NewGuid(), Buyer, false, Now.AddDays(1))),
        ("reject", QuoteVersionStatus.Sent, QuoteVersionStatus.Rejected, q => q.Reject(1, Buyer, null, Now.AddDays(1))),
        ("withdraw", QuoteVersionStatus.Sent, QuoteVersionStatus.Withdrawn, q => q.Withdraw(1, Sender, "Wrong prices", Now.AddDays(1))),
    ];

    [Fact]
    public void EveryCommand_WorksOnlyFromItsStatusInTheTable_AndARefusalChangesNothing()
    {
        foreach (var (name, allowedFrom, to, _) in Commands) Section3.Should().Contain((allowedFrom, to), name);
        foreach (var from in AllStatuses)
            foreach (var (name, allowedFrom, to, run) in Commands)
            {
                var quote = WithVersion1In(from);
                var before = Fingerprint(quote.Version(1));
                var act = () => run(quote);
                if (from == allowedFrom)
                {
                    act.Should().NotThrow($"{name} from {from}");
                    quote.Version(1).Status.Should().Be(to, $"{name} from {from}");
                }
                else
                {
                    act.Should().Throw<DomainException>($"{name} from {from} is outside §3");
                    quote.Version(1).Status.Should().Be(from);
                    Fingerprint(quote.Version(1)).Should().Be(before, $"a refused {name} from {from} changes nothing");
                }
            }
    }

    [Theory]
    [InlineData(QuoteVersionStatus.PendingApproval)]
    [InlineData(QuoteVersionStatus.Sent)]
    [InlineData(QuoteVersionStatus.Accepted)]
    [InlineData(QuoteVersionStatus.Rejected)]
    [InlineData(QuoteVersionStatus.Withdrawn)]
    [InlineData(QuoteVersionStatus.Superseded)]
    public void AnIssuedVersionCannotBeEdited(QuoteVersionStatus status)
    {
        var quote = WithVersion1In(status);
        var before = Fingerprint(quote.Version(1));

        var act = () => quote.EditDraft(1, Content(Line(V1, price: 1)), Staff(Editor), Options, Now.AddDays(1));

        act.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_NOT_EDITABLE");
        Fingerprint(quote.Version(1)).Should().Be(before);
        quote.Version(1).Contributors.Select(c => c.UserId).Should().NotContain(Editor);
    }

    [Fact]
    public void ADraftCanBeEdited_AndEveryEditorBecomesAContributor()
    {
        var quote = Drafted();
        quote.EditDraft(1, Content(7, Line(V3, qty: 2, price: 5_000, note: "Delivered in 3 weeks")), Staff(Editor), Options, Now.AddHours(1));

        var v = quote.Version(1);
        v.ValidityDays.Should().Be(7);
        v.Lines.Should().ContainSingle().Which.Should().Match<QuoteLine>(l =>
            l.LineNo == 1 && l.VariantId == V3 && l.Quantity == 2 && l.UnitPriceMinor == 5_000 && l.LineNote == "Delivered in 3 weeks");
        v.Contributors.Select(c => c.UserId).Should().BeEquivalentTo([Author, Editor]);
    }

    // ---- send, approve, supersede ------------------------------------------------------------------------------------

    [Fact]
    public void SendWithoutFlags_SendsAtOnce_AndSendWithFlags_WaitsForApproval()
    {
        var direct = Drafted();
        direct.Send(1, Issue(direct), Staff(Sender), false, Options, Now).Should().Be(QuoteVersionStatus.Sent);
        direct.Version(1).Should().Match<QuoteVersion>(v => v.SentBy == Sender && v.SentAtUtc == Now && v.SubmittedBy == Sender
            && QuoteVersion.IsHash(v.ContentHash) && v.ContentHash == QuoteContentHash.Compute(direct, v)
            && v.CompanySnapshot == Company && v.PreparerName == "Rahim Uddin");
        direct.DomainEvents.OfType<QuoteSentEvent>().Should().ContainSingle();

        var flagged = Drafted();
        flagged.Send(1, Issue(flagged, overAuthority: true), Staff(Sender), false, Options, Now).Should().Be(QuoteVersionStatus.PendingApproval);
        flagged.Version(1).Should().Match<QuoteVersion>(v => v.SentAtUtc == null && v.ValidUntilUtc == null && QuoteVersion.IsHash(v.ContentHash));
        flagged.DomainEvents.OfType<QuoteApprovalRequestedEvent>().Should().ContainSingle();
        flagged.DomainEvents.OfType<QuoteSentEvent>().Should().BeEmpty();
    }

    [Fact]
    public void Approval_SendsTheVersion_AndNeedsTheHashTheApproverWasShown()
    {
        var quote = Pending();
        var stale = () => ApproveAs(quote, 1, Now.AddHours(1), hash: Hash('f'));
        stale.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.PendingApproval);

        ApproveAs(quote, 1, Now.AddHours(1), comment: "Within the project budget");
        quote.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Sent && v.ApprovedBy == Approver
            && v.SentBy == Approver && v.SentAtUtc == Now.AddHours(1) && v.ApprovalComment == "Within the project budget");
        quote.DomainEvents.OfType<QuoteSentEvent>().Should().ContainSingle();
    }

    [Fact]
    public void RejectingTheApproval_ReturnsADraft_ThatCanBeEditedAndSentAgain()
    {
        var quote = Pending();
        quote.RejectApproval(1, Approver, "Five percent is the most", Now.AddHours(1));
        quote.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Draft && v.ContentHash == null
            && v.SubmittedBy == null && v.ApprovalRejectedBy == Approver && v.ApprovalComment == "Five percent is the most");

        quote.EditDraft(1, Content(Line(V1, price: 110_000)), Staff(Author), Options, Now.AddHours(2));
        quote.Send(1, Issue(quote), Staff(Sender), false, Options, Now.AddHours(2)).Should().Be(QuoteVersionStatus.Sent);
    }

    [Fact]
    public void ANewerVersion_SupersedesTheSentOne_OnlyWhenItIsSent()
    {
        var quote = Sent();
        quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Sent, "the old version stays acceptable until the new one is sent");
        quote.IsAcceptable(1, false, Now.AddHours(1)).Should().BeTrue();

        quote.Send(2, Issue(quote, 2), Staff(Sender), false, Options, Now.AddHours(2));
        quote.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Superseded && v.SupersededAtUtc == Now.AddHours(2));
        quote.LiveVersion!.VersionNo.Should().Be(2);
        quote.IsAcceptable(1, false, Now.AddHours(2)).Should().BeFalse();
    }

    [Fact]
    public void AnApprovedNewerVersion_AlsoSupersedesTheSentOne()
    {
        var quote = Sent();
        quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
        quote.Send(2, Issue(quote, 2, overAuthority: true), Staff(Sender), false, Options, Now.AddHours(2));
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Sent);

        ApproveAs(quote, 2, Now.AddHours(3));
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Superseded);
        quote.Versions.Count(v => v.Status is QuoteVersionStatus.Sent or QuoteVersionStatus.Accepted).Should().Be(1);
    }

    // ---- one open version, revise -----------------------------------------------------------------------------------

    [Theory]
    [InlineData(QuoteVersionStatus.Draft)]
    [InlineData(QuoteVersionStatus.PendingApproval)]
    public void Revise_IsRefusedWhileAVersionIsOpen(QuoteVersionStatus open)
    {
        var quote = WithVersion1In(open);
        var act = () => quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
        act.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_OPEN_VERSION_EXISTS");
        quote.Versions.Should().ContainSingle();
    }

    [Fact]
    public void Revise_IsRefusedASecondTime_SoThereIsAtMostOneOpenVersion()
    {
        var quote = Sent();
        quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
        var again = () => quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(2));
        again.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_OPEN_VERSION_EXISTS");
        quote.Versions.Count(v => v.Status is QuoteVersionStatus.Draft or QuoteVersionStatus.PendingApproval).Should().Be(1);
    }

    [Theory]
    [InlineData(QuoteVersionStatus.Sent)]
    [InlineData(QuoteVersionStatus.Rejected)]
    [InlineData(QuoteVersionStatus.Withdrawn)]
    public void Revise_CopiesTheLatestVersionIntoANewDraft_AndLeavesTheIssuedOneUntouched(QuoteVersionStatus status)
    {
        var quote = WithVersion1In(status);
        var before = Fingerprint(quote.Version(1));
        var newId = Guid.NewGuid();

        var v2 = quote.Revise(newId, Editor, false, Now.AddDays(2));

        (v2.Id, v2.VersionNo, v2.Status, v2.CreatedBy, quote.LatestVersionNo).Should().Be((newId, 2, QuoteVersionStatus.Draft, Editor, 2));
        v2.Contributors.Select(c => c.UserId).Should().Equal(Editor);
        v2.ContentHash.Should().BeNull();
        (v2.ValidityDays, v2.PaymentTerms, v2.TermsText, v2.BuyerNote, v2.InternalNote).Should()
            .Be((15, QuotePaymentTerms.AsAccount, "Prices in BDT.", "As discussed on the phone.", "Margin is thin; check with finance."));
        v2.Lines.Select(l => (l.LineNo, l.VariantId, l.Quantity, l.UnitPriceMinor))
            .Should().Equal(quote.Version(1).Lines.Select(l => (l.LineNo, l.VariantId, l.Quantity, l.UnitPriceMinor)));
        v2.Lines.Select(l => l.Id).Should().NotIntersectWith(quote.Version(1).Lines.Select(l => l.Id));
        Fingerprint(quote.Version(1)).Should().Be(before);
    }

    // ---- accept, re-accept, expiry --------------------------------------------------------------------------------

    [Fact]
    public void Accept_RecordsTheOrder_AndNeedsTheHashTheBuyerSaw()
    {
        var quote = Sent();
        var order = Guid.NewGuid();
        var stale = () => quote.Accept(1, Hash('9'), order, Buyer, false, Now.AddHours(1));
        stale.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");

        quote.Accept(1, HashOf(quote), order, Buyer, false, Now.AddHours(1));
        quote.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Accepted && v.AcceptedOrderId == order
            && v.AcceptedBy == Buyer && v.AcceptedAtUtc == Now.AddHours(1));
        quote.DomainEvents.OfType<QuoteAcceptedEvent>().Should().ContainSingle().Which.OrderId.Should().Be(order);

        var twice = () => quote.Accept(1, HashOf(quote), Guid.NewGuid(), Buyer, false, Now.AddHours(2));
        twice.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_ALREADY_ACCEPTED");
        quote.Version(1).AcceptedOrderId.Should().Be(order);
    }

    [Fact]
    public void AQuoteWhoseOrderLapsedBeforeConfirmation_CanBeAcceptedAgain_RevisedAndSuperseded()
    {
        var quote = WithVersion1In(QuoteVersionStatus.Accepted);
        quote.IsAcceptable(1, acceptedOrderLapsed: false, Now.AddHours(2)).Should().BeFalse();
        quote.IsAcceptable(1, acceptedOrderLapsed: true, Now.AddHours(2)).Should().BeTrue();

        var second = Guid.NewGuid();
        quote.Accept(1, HashOf(quote), second, Buyer, acceptedOrderLapsed: true, Now.AddHours(2));
        quote.Version(1).AcceptedOrderId.Should().Be(second);

        var revise = () => quote.Revise(Guid.NewGuid(), Editor, acceptedOrderLapsed: false, Now.AddHours(3));
        revise.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_ALREADY_ACCEPTED");

        quote.Revise(Guid.NewGuid(), Editor, acceptedOrderLapsed: true, Now.AddHours(3));
        var sendWhileLive = () => quote.Send(2, Issue(quote, 2), Staff(Sender), acceptedOrderLapsed: false, Options, Now.AddHours(4));
        sendWhileLive.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_ALREADY_ACCEPTED");
        quote.Version(2).Status.Should().Be(QuoteVersionStatus.Draft, "a refused send freezes nothing");

        quote.Send(2, Issue(quote, 2), Staff(Sender), acceptedOrderLapsed: true, Options, Now.AddHours(4));
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Superseded);
    }

    [Fact]
    public void AnExpiredVersion_CannotBeAccepted_ButCanBeRejectedOrWithdrawn()
    {
        var quote = Sent();
        var until = quote.Version(1).ValidUntilUtc!.Value;
        quote.Version(1).IsExpired(until.AddTicks(-1)).Should().BeFalse();
        quote.Version(1).IsExpired(until).Should().BeTrue();

        var late = () => quote.Accept(1, HashOf(quote), Guid.NewGuid(), Buyer, false, until);
        late.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_NOT_ACCEPTABLE");
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Sent);

        quote.Accept(1, HashOf(quote), Guid.NewGuid(), Buyer, false, until.AddTicks(-1));
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Accepted);
    }

    [Fact]
    public void Reject_KeepsTheBuyersReason_AndTheQuoteStaysOpenForARevision()
    {
        var quote = Sent();
        quote.Reject(1, Buyer, "  Delivery must be within two weeks  ", Now.AddHours(1));
        quote.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Rejected && v.RejectedBy == Buyer
            && v.RejectReason == "Delivery must be within two weeks");
        quote.DomainEvents.OfType<QuoteRejectedEvent>().Should().ContainSingle();
        quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(2)).VersionNo.Should().Be(2);
    }

    // ---- close --------------------------------------------------------------------------------------------------------

    [Fact]
    public void Close_WithdrawsTheSentVersion_AndTheQuoteGetsNothingNew()
    {
        var quote = Sent();
        quote.Close(Sender, "Customer bought elsewhere", Now.AddHours(1));

        quote.IsClosed.Should().BeTrue();
        quote.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Withdrawn && v.WithdrawnBy == Sender
            && v.WithdrawReason == "Customer bought elsewhere");
        quote.DomainEvents.OfType<QuoteWithdrawnEvent>().Should().ContainSingle();

        FluentActions.Invoking(() => quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(2)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CLOSED");
        FluentActions.Invoking(() => quote.Close(Sender, "Again please", Now.AddHours(2)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CLOSED");
    }

    [Fact]
    public void Close_RecallsAPendingVersion_AndAClosedQuoteCannotBeSentEditedOrAccepted()
    {
        var pending = Pending();
        pending.Close(Sender, "No longer needed", Now.AddHours(1));
        pending.Version(1).Status.Should().Be(QuoteVersionStatus.Draft);
        FluentActions.Invoking(() => pending.Send(1, Issue(pending), Staff(Sender), false, Options, Now.AddHours(2)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CLOSED");
        FluentActions.Invoking(() => pending.EditDraft(1, Content(Line(V1)), Staff(Author), Options, Now.AddHours(2)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CLOSED");

        var accepted = WithVersion1In(QuoteVersionStatus.Accepted);
        accepted.Close(Sender, "Order lapsed, project cancelled", Now.AddHours(2));
        accepted.IsAcceptable(1, acceptedOrderLapsed: true, Now.AddHours(3)).Should().BeFalse();
        FluentActions.Invoking(() => accepted.Accept(1, HashOf(accepted), Guid.NewGuid(), Buyer, true, Now.AddHours(3)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_NOT_ACCEPTABLE");
    }

    [Theory]
    [InlineData("")]
    [InlineData("abcd")]
    public void Close_NeedsAReason(string reason) =>
        FluentActions.Invoking(() => Sent().Close(Sender, reason, Now)).Should().Throw<ValidationException>();

    /// <summary>Everything a buyer could see of a version, plus its status fields: equal fingerprints = nothing changed.</summary>
    internal static string Fingerprint(QuoteVersion v) =>
        string.Join("|", v.Status, v.ValidityDays, v.PaymentTerms, v.ShippingMode, v.ShipToSiteId, v.TermsText, v.BuyerNote, v.InternalNote,
            v.ContentHash, v.CompanySnapshot, v.PreparerName, v.SubmittedBy, v.SentAtUtc, v.ValidUntilUtc, v.AcceptedOrderId, v.ApprovedBy,
            v.GoodsNetMinor, v.TaxMinor, v.GoodsTotalMinor, v.ShippingTotalMinor, v.GrandTotalMinor,
            v.Flags is { } f ? $"{string.Join(",", f.BelowFloorLineIds)}/{string.Join(",", f.OverAuthorityLineIds)}/{f.LongValidity}" : "-",
            string.Join(";", v.Contributors.Select(c => c.UserId)),
            string.Join(";", v.ShippingFees?.Select(f => $"{f.SellerId}:{f.ShippingMinor}") ?? []),
            string.Join(";", v.Lines.Select(l => $"{l.Id}:{l.LineNo}:{l.VariantId}:{l.Quantity}:{l.UnitPriceMinor}:{l.LineNote}:{l.BelowFloorReason}:"
                + $"{l.Sku}:{l.PartNumber}:{l.TitleEn}:{l.TitleBn}:{l.SellerId}:{l.VatRate}:{l.PublicUnitPriceMinor}:"
                + $"{l.LineNetMinor}:{l.LineTaxMinor}:{l.LineTotalMinor}:{l.ReferenceUnitPriceMinor}:{l.ReferenceSource}:{l.CostMinorAtCheck}:"
                + $"{l.FloorMinorAtCheck}:{l.BelowFloor}:{l.OverAuthority}")));
}
