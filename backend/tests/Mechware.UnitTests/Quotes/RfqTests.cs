using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>D4.2: the RFQ of ADR-0011 §2 (sources, line rules, statuses).</summary>
public class RfqTests
{
    private static readonly RfqStatus[] AllStatuses = Enum.GetValues<RfqStatus>();
    private const string Key = "rfq-key-0001";

    private static RfqLineInput Matched(Guid variant, int qty = 5) => new("6ES7214-1AG40-0XB0 x5", "6ES72141AG400XB0", qty, RfqMatchStatus.Matched, variant, null, null);
    private static RfqLineInput Unmatched(string text = "Danfoss FC-302 7.5 kW", int qty = 1) => new(text, null, qty, RfqMatchStatus.NotFound, null, null, null);

    private static Rfq Submit(RfqSource source = RfqSource.Text, IReadOnlyList<RfqLineInput>? lines = null, string? message = "Needed for the new line",
        DateOnly? neededBy = null, DateTime? at = null, string key = Key, string? hash = null, string? number = null) =>
        Rfq.Submit(Guid.NewGuid(), number ?? QuoteNumber.NewRfq(at ?? Now), CompanyA, Buyer, source, message, neededBy, null,
            lines ?? [Matched(V1), Unmatched()], key, hash ?? Hash('1'), at ?? Now);

    [Fact]
    public void ASubmittedRfq_KeepsWhatTheBuyerAsked_NumbersItsLines_AndRaisesItsEvent()
    {
        var rfq = Submit(neededBy: new DateOnly(2026, 12, 1));
        rfq.Should().Match<Rfq>(r => r.Status == RfqStatus.Submitted && r.CompanyId == CompanyA && r.RequestedBy == Buyer
            && r.Source == RfqSource.Text && r.LineCount == 2 && r.Message == "Needed for the new line" && r.NeededBy == new DateOnly(2026, 12, 1)
            && r.IdempotencyKey == Key && r.RequestHash == Hash('1'));
        rfq.Lines.Select(l => (l.LineNo, l.MatchStatus, l.VariantId)).Should().Equal((1, RfqMatchStatus.Matched, (Guid?)V1), (2, RfqMatchStatus.NotFound, null));
        rfq.DomainEvents.OfType<RfqSubmittedEvent>().Should().ContainSingle().Which.Should().Match<RfqSubmittedEvent>(e => e.RfqId == rfq.Id && e.CompanyId == CompanyA);
    }

    public static TheoryData<string, RfqLineInput[]> BadLines => new()
    {
        { "no lines", [] },
        { "501 lines", Enumerable.Range(1, 501).Select(i => Unmatched($"part {i}")).ToArray() },
        { "quantity 0", [Unmatched(qty: 0)] },
        { "quantity above the order cap", [Unmatched(qty: 10_001)] },
        { "blank text", [Unmatched(" ")] },
        { "text above 200", [Unmatched(new string('p', 201))] },
        { "part key above 64", [Unmatched() with { PartKey = new string('K', 65) }] },
        { "note above 500", [Unmatched() with { Note = new string('n', 501) }] },
        { "matched without a variant", [Matched(V1) with { VariantId = null }] },
        { "not found with a variant", [Unmatched() with { VariantId = V1 }] },
        { "ambiguous with a variant", [Unmatched() with { MatchStatus = RfqMatchStatus.Ambiguous, VariantId = V1 }] },
        { "successor on a matched line", [Matched(V1) with { SuccessorVariantId = V2 }] },
        { "unknown match status", [Unmatched() with { MatchStatus = (RfqMatchStatus)42 }] },
    };

    [Theory]
    [MemberData(nameof(BadLines))]
    public void ABadLine_RefusesTheWholeRfq(string why, RfqLineInput[] lines) =>
        FluentActions.Invoking(() => Submit(lines: lines)).Should().Throw<ValidationException>(why);

    [Fact]
    public void FiveHundredLines_AndAnObsoleteLineWithItsSuccessor_AreAccepted()
    {
        Submit(lines: Enumerable.Range(1, 500).Select(i => Unmatched($"part {i}")).ToArray()).LineCount.Should().Be(500);
        var obsolete = new RfqLineInput("old relay", "OLDRELAY", 2, RfqMatchStatus.Obsolete, V1, V2, "or equivalent");
        Submit(lines: [obsolete]).Lines.Single().Should().Match<RfqLine>(l => l.SuccessorVariantId == V2 && l.Note == "or equivalent");
    }

    [Fact]
    public void AProductRfq_HasExactlyOneMatchedLine()
    {
        Submit(RfqSource.Product, [Matched(V1)]).Source.Should().Be(RfqSource.Product);
        FluentActions.Invoking(() => Submit(RfqSource.Product, [Matched(V1), Matched(V2)])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Submit(RfqSource.Product, [Unmatched()])).Should().Throw<ValidationException>();
    }

    [Fact]
    public void NeededBy_IsADhakaDateFromTodayToAYearAhead()
    {
        var at = new DateTime(2026, 11, 17, 19, 0, 0, DateTimeKind.Utc); // 01:00 on 18 Nov in Dhaka
        FluentActions.Invoking(() => Submit(neededBy: new DateOnly(2026, 11, 17), at: at)).Should().Throw<ValidationException>("17 Nov is yesterday in Dhaka");
        Submit(neededBy: new DateOnly(2026, 11, 18), at: at).NeededBy.Should().Be(new DateOnly(2026, 11, 18));
        Submit(neededBy: new DateOnly(2027, 11, 18), at: at).NeededBy.Should().Be(new DateOnly(2027, 11, 18));
        FluentActions.Invoking(() => Submit(neededBy: new DateOnly(2027, 11, 19), at: at)).Should().Throw<ValidationException>();
    }

    [Fact]
    public void TheHeader_IsValidated()
    {
        FluentActions.Invoking(() => Submit(message: new string('m', 2_001))).Should().Throw<ValidationException>();
        Submit(message: "   ").Message.Should().BeNull();
        FluentActions.Invoking(() => Submit(key: "short")).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Submit(key: new string('k', 101))).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Submit(hash: "not-a-hash")).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Submit(number: "MWQ-261117-K7Q2MX")).Should().Throw<ValidationException>("a quote number is not an RFQ number");
        FluentActions.Invoking(() => Submit(source: (RfqSource)9)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Rfq.Submit(Guid.NewGuid(), QuoteNumber.NewRfq(Now), Guid.Empty, Buyer, RfqSource.Text, null, null, null,
            [Unmatched()], Key, Hash('1'), Now)).Should().Throw<ValidationException>();
    }

    // ---- statuses (§2) ------------------------------------------------------------------------------------------------

    private static readonly HashSet<(RfqStatus, RfqStatus)> Section2 =
    [
        (RfqStatus.Submitted, RfqStatus.Quoting), (RfqStatus.Quoting, RfqStatus.Answered),
        (RfqStatus.Submitted, RfqStatus.Closed), (RfqStatus.Quoting, RfqStatus.Closed), (RfqStatus.Answered, RfqStatus.Closed),
        (RfqStatus.Submitted, RfqStatus.Withdrawn), (RfqStatus.Quoting, RfqStatus.Withdrawn), (RfqStatus.Answered, RfqStatus.Withdrawn),
    ];

    [Fact]
    public void TheRfqTransitionTable_IsExactlySection2()
    {
        foreach (var from in AllStatuses)
            foreach (var to in AllStatuses)
                Rfq.CanMove(from, to).Should().Be(Section2.Contains((from, to)), $"{from} → {to}");
    }

    private static Rfq InStatus(RfqStatus status)
    {
        var rfq = Submit();
        if (status is RfqStatus.Quoting or RfqStatus.Answered) rfq.StartQuoting(Now);
        if (status == RfqStatus.Answered) rfq.RecordQuoteSent(Now.AddHours(2));
        if (status == RfqStatus.Closed) rfq.Close(Sender, "No bid: discontinued", Now);
        if (status == RfqStatus.Withdrawn) rfq.Withdraw(Now);
        return rfq;
    }

    [Fact]
    public void QuotingAndAnswering_FollowTheQuotes_AndMeasureTheFirstAnswerOnce()
    {
        var rfq = InStatus(RfqStatus.Submitted);
        rfq.StartQuoting(Now.AddMinutes(10));
        rfq.Status.Should().Be(RfqStatus.Quoting);
        rfq.StartQuoting(Now.AddMinutes(20)); // a second quote from the same RFQ
        rfq.Status.Should().Be(RfqStatus.Quoting);

        rfq.RecordQuoteSent(Now.AddHours(3));
        rfq.RecordQuoteSent(Now.AddHours(5));
        (rfq.Status, rfq.FirstQuoteSentAtUtc).Should().Be((RfqStatus.Answered, (DateTime?)Now.AddHours(3)));
        rfq.StartQuoting(Now.AddHours(6));
        rfq.Status.Should().Be(RfqStatus.Answered, "another quote from an answered RFQ does not reopen it");
    }

    [Theory]
    [InlineData(RfqStatus.Closed)]
    [InlineData(RfqStatus.Withdrawn)]
    public void AnEndedRfq_TakesNoNewQuoteAndCannotBeClosedOrWithdrawn(RfqStatus status)
    {
        var rfq = InStatus(status);
        foreach (var act in new Action[] { () => rfq.StartQuoting(Now), () => rfq.Close(Sender, "Closing again", Now), () => rfq.Withdraw(Now), () => rfq.Assign(Sender, Now) })
            act.Should().Throw<ConflictException>().Which.Code.Should().Be("RFQ_INVALID_TRANSITION");
        rfq.Status.Should().Be(status);
    }

    [Fact]
    public void AQuoteSentAfterAWithdrawal_LeavesTheRfqWithdrawn()
    {
        var rfq = InStatus(RfqStatus.Quoting);
        rfq.Withdraw(Now.AddHours(1));
        rfq.RecordQuoteSent(Now.AddHours(2));
        (rfq.Status, rfq.WithdrawnAtUtc).Should().Be((RfqStatus.Withdrawn, (DateTime?)Now.AddHours(1)));
    }

    [Fact]
    public void Close_NeedsAReason_ThatTheBuyerSees_AndAssignWorksWhileOpen()
    {
        var rfq = InStatus(RfqStatus.Answered);
        FluentActions.Invoking(() => rfq.Close(Sender, "no", Now)).Should().Throw<ValidationException>();
        rfq.Assign(Sender, Now);
        rfq.AssignedTo.Should().Be(Sender);
        rfq.Close(Sender, "  Quoted in MWQ-261117-K7Q2MX  ", Now.AddHours(1));
        rfq.Should().Match<Rfq>(r => r.Status == RfqStatus.Closed && r.ClosedBy == Sender && r.CloseReason == "Quoted in MWQ-261117-K7Q2MX");
    }
}
