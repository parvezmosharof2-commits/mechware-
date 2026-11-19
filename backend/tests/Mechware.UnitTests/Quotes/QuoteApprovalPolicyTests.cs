using System.Reflection;
using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.3: the approval policy of ADR-0011 §5 (F1 floor, F2 cost, F3 authority, F4 validity) and the four-eyes approval in the
/// domain (threat model QT7, QT5 threshold half, QT6 conflict-of-interest domain half). The API halves are D4.8.
/// </summary>
public class QuoteApprovalPolicyTests
{
    /// <summary>One line (V1, 10 × <paramref name="price"/>) drafted by <see cref="Author"/>.</summary>
    private static Quote OneLine(long price, string? reason = null, int validityDays = 15)
    {
        var quote = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), CompanyA, null, Contact, Author, Options, Now);
        quote.EditDraft(1, Content(validityDays, Line(V1, price: price, reason: reason)), Staff(Author), Options, Now);
        return quote;
    }

    private static QuoteApprovalCheck Evaluate(Quote quote, long reference, long? cost = 1, IReadOnlyCollection<MarginFloorRow>? floors = null,
        bool allowBelowCost = false, QuoteOptions? options = null, int versionNo = 1)
    {
        var v = quote.Version(versionNo);
        return QuoteApprovalPolicy.Evaluate(v, v.Lines.Select(l => Cost(l.VariantId, cost)).ToList(), floors ?? ZeroFloor,
            v.Lines.Select(l => Reference(l.VariantId, reference, PriceSource.Contract)).ToList(), options ?? Options, allowBelowCost);
    }

    private static QuoteVersionStatus SendWith(Quote quote, QuoteApprovalCheck check, int versionNo = 1, Guid? sender = null) =>
        quote.Send(versionNo, Issue(quote, versionNo) with { Check = check }, Staff(sender ?? Sender), false, Options, Now);

    private static Guid LineId(Quote quote, int lineNo = 1, int versionNo = 1) => quote.Version(versionNo).Lines.Single(l => l.LineNo == lineNo).Id;

    // ---- F3 discount authority --------------------------------------------------------------------------------------

    [Theory]
    // unit × 10,000 < reference × (10,000 − 500): equal is within authority.
    [InlineData(95_000, 100_000, 500, false)]
    [InlineData(94_999, 100_000, 500, true)]
    [InlineData(100_000, 100_000, 500, false)]
    [InlineData(120_000, 100_000, 500, false)] // above the buyer's price: never a flag (§15 decision 10)
    [InlineData(95_000, 99_999, 500, false)] // the limit is 94,999.05: 950,000,000 is not below 949,990,500
    [InlineData(94_999, 99_999, 500, true)] // 949,990,000 < 949,990,500
    [InlineData(99_999, 100_000, 0, true)] // no authority: any discount needs approval
    [InlineData(10, 100_000, 9_999, false)] // 99.99 % authority: the limit is 10 poisha
    [InlineData(9, 100_000, 9_999, true)]
    public void TheAuthorityRule_IsStrictlyBelowTheLimit_InIntegers(long unit, long reference, int basisPoints, bool over) =>
        QuoteApprovalPolicy.IsOverAuthority(unit, reference, basisPoints).Should().Be(over);

    [Fact]
    public void ADiscountBeyondTheStaffLimitNeedsASecondApprover()
    {
        var within = OneLine(95_000); // exactly 5 % below the buyer's 100,000
        var withinCheck = Evaluate(within, reference: 100_000);
        withinCheck.Flags.NeedsApproval.Should().BeFalse();
        SendWith(within, withinCheck).Should().Be(QuoteVersionStatus.Sent);

        var beyond = OneLine(94_999);
        var check = Evaluate(beyond, reference: 100_000);
        check.Flags.OverAuthorityLineIds.Should().Equal(LineId(beyond));
        check.Flags.BelowFloorLineIds.Should().BeEmpty();
        SendWith(beyond, check).Should().Be(QuoteVersionStatus.PendingApproval, "one person cannot give more than the authority");
        beyond.Version(1).Lines.Single().Should().Match<QuoteLine>(l => l.OverAuthority && !l.BelowFloor
            && l.ReferenceUnitPriceMinor == 100_000 && l.ReferenceSource == PriceSource.Contract && l.CostMinorAtCheck == 1 && l.FloorMinorAtCheck == 1);

        var self = () => ApproveAs(beyond, 1, Now.AddHours(1), approver: Sender);
        self.Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_FOUR_EYES");
        ApproveAs(beyond, 1, Now.AddHours(1));
        beyond.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Sent && v.ApprovedBy == Approver);
    }

    [Fact]
    public void TheDiscountIsMeasuredAgainstTheBuyersResolverPrice_NotThePreviousVersion()
    {
        // v1 at the buyer's price; v2 at 5 % below v1 (within); v3 at 5 % below v2, which is 9.75 % below the buyer's price.
        var quote = OneLine(100_000);
        SendWith(quote, Evaluate(quote, reference: 100_000)).Should().Be(QuoteVersionStatus.Sent);
        quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
        quote.EditDraft(2, Content(Line(V1, price: 95_000)), Staff(Editor), Options, Now.AddHours(1));
        SendWith(quote, Evaluate(quote, reference: 100_000, versionNo: 2), 2).Should().Be(QuoteVersionStatus.Sent);
        quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(2));
        quote.EditDraft(3, Content(Line(V1, price: 90_250)), Staff(Editor), Options, Now.AddHours(2));

        var check = Evaluate(quote, reference: 100_000, versionNo: 3);
        check.Flags.OverAuthorityLineIds.Should().Equal([LineId(quote, versionNo: 3)], "no ratchet: the reference stays the buyer's own price");
        SendWith(quote, check, 3).Should().Be(QuoteVersionStatus.PendingApproval);

        // And the other way: a contract price of 90,000 makes 86,000 within authority, though it is 14 % below version 1.
        var contract = OneLine(100_000);
        SendWith(contract, Evaluate(contract, reference: 100_000));
        contract.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1));
        contract.EditDraft(2, Content(Line(V1, price: 86_000)), Staff(Editor), Options, Now.AddHours(1));
        Evaluate(contract, reference: 90_000, versionNo: 2).Flags.NeedsApproval.Should().BeFalse();
    }

    // ---- F1 floor and F2 cost ---------------------------------------------------------------------------------------

    [Fact]
    public void ALineWithoutCost_IsTreatedAsBelowFloor()
    {
        foreach (var (why, cost, floors) in new (string, long?, MarginFloorRow[])[]
                 {
                     ("no cost", null, TenPercentFloor), ("cost 0", 0, TenPercentFloor), ("negative cost", -5, TenPercentFloor),
                     ("no global floor row", 50_000, [new(MarginFloorScope.Category, null, Guid.NewGuid(), 5m)]),
                 })
        {
            var quote = OneLine(100_000);
            var check = Evaluate(quote, reference: 100_000, cost: cost, floors: floors);
            check.Flags.BelowFloorLineIds.Should().Equal([LineId(quote)], why);
            check.Lines.Single().Should().Match<QuoteLineCheck>(c => c.BelowFloor && !c.BelowCost && c.FloorMinor == null, why);
            FluentActions.Invoking(() => SendWith(quote, check)).Should().Throw<ValidationException>(why)
                .Which.Errors.Should().ContainSingle(e => e.Field == "belowFloorReason");
        }

        var noFacts = OneLine(100_000, reason: "Cost not loaded yet; owner agreed");
        var v = noFacts.Version(1);
        var missing = QuoteApprovalPolicy.Evaluate(v, [], TenPercentFloor, [Reference(V1, 100_000)], Options, false);
        missing.Flags.BelowFloorLineIds.Should().Equal([LineId(noFacts)], "a variant with no cost facts at all fails closed");
        SendWith(noFacts, missing).Should().Be(QuoteVersionStatus.PendingApproval);
    }

    [Theory]
    // cost 101, 10 %: 111.1 rounds UP to 112, so 111 is below the floor and 112 is not.
    [InlineData(111, true)]
    [InlineData(112, false)]
    public void TheFloor_IsRoundedUp_SoRoundingNeverLetsAPriceUnderIt(long price, bool below)
    {
        var quote = OneLine(price, reason: "Clearance of old stock");
        var check = Evaluate(quote, reference: price, cost: 101, floors: TenPercentFloor);
        check.Lines.Single().Should().Match<QuoteLineCheck>(c => c.FloorMinor == 112 && c.CostMinor == 101 && c.BelowFloor == below && !c.BelowCost);
    }

    [Fact]
    public void ALineBelowCost_IsRefused_UnlessConfigAllows()
    {
        var quote = OneLine(99_999, reason: "Matching a competitor's tender");
        var check = Evaluate(quote, reference: 100_000, cost: 100_000, floors: TenPercentFloor);
        check.BelowCostLineIds.Should().Equal(LineId(quote));
        var before = QuoteLifecycleTests.Fingerprint(quote.Version(1));
        FluentActions.Invoking(() => SendWith(quote, check)).Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_BELOW_COST");
        QuoteLifecycleTests.Fingerprint(quote.Version(1)).Should().Be(before, "a refused send freezes nothing");

        // Exactly at cost is not below cost (it is below the floor).
        var atCost = OneLine(100_000, reason: "Matching a competitor's tender");
        Evaluate(atCost, reference: 100_000, cost: 100_000, floors: TenPercentFloor).BelowCostLineIds.Should().BeEmpty();

        // Development/Testing may allow it (Pricing:AllowBelowCost): then it is a below-floor line like any other.
        var allowed = Evaluate(quote, reference: 100_000, cost: 100_000, floors: TenPercentFloor, allowBelowCost: true);
        allowed.BelowCostLineIds.Should().BeEmpty();
        allowed.Lines.Single().BelowCost.Should().BeTrue();
        SendWith(quote, allowed).Should().Be(QuoteVersionStatus.PendingApproval);
        quote.Version(1).Flags!.BelowFloorLineIds.Should().Equal(LineId(quote));
    }

    // ---- F4 validity --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(30, false)]
    [InlineData(31, true)]
    [InlineData(90, true)]
    public void ValidityAboveTheMaximum_IsRefused_AndAboveTheThreshold_NeedsApproval(int days, bool needsApproval)
    {
        var quote = OneLine(100_000, validityDays: days);
        var check = Evaluate(quote, reference: 100_000);
        check.Flags.LongValidity.Should().Be(needsApproval);
        check.Flags.NeedsApproval.Should().Be(needsApproval);
        SendWith(quote, check).Should().Be(needsApproval ? QuoteVersionStatus.PendingApproval : QuoteVersionStatus.Sent);

        var tighter = Options with { ValidityWithoutApprovalDays = days };
        Evaluate(OneLine(100_000, validityDays: days), reference: 100_000, options: tighter).Flags.LongValidity.Should().BeFalse();
        FluentActions.Invoking(() => OneLine(100_000, validityDays: 91)).Should().Throw<ValidationException>("the maximum half is D4.2");
    }

    // ---- inputs ---------------------------------------------------------------------------------------------------------

    [Fact]
    public void ThePolicy_NeedsOneNetReferencePerLine_AndCostFactsOncePerVariant()
    {
        var quote = Drafted();
        var v = quote.Version(1);
        var costs = new[] { Cost(V1), Cost(V2) };
        var refs = new[] { Reference(V1, 100_000), Reference(V2, 250_000) };
        FluentActions.Invoking(() => QuoteApprovalPolicy.Evaluate(v, costs, ZeroFloor, refs[..1], Options, false))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");
        FluentActions.Invoking(() => QuoteApprovalPolicy.Evaluate(v, costs, ZeroFloor, [refs[0], Reference(V3, 1)], Options, false))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");
        FluentActions.Invoking(() => QuoteApprovalPolicy.Evaluate(v, costs, ZeroFloor, [refs[0], refs[1], refs[1]], Options, false))
            .Should().Throw<ValidationException>();
        FluentActions.Invoking(() => QuoteApprovalPolicy.Evaluate(v, costs, ZeroFloor,
                [refs[0], refs[1] with { Basis = PriceBasis.GrossInclVat }], Options, false))
            .Should().Throw<ValidationException>("the reference is the Exclusive (net) resolver price");
        FluentActions.Invoking(() => QuoteApprovalPolicy.Evaluate(v, costs, ZeroFloor, [refs[0], refs[1] with { UnitPriceMinor = 0 }], Options, false))
            .Should().Throw<ValidationException>();
        FluentActions.Invoking(() => QuoteApprovalPolicy.Evaluate(v, [.. costs, Cost(V1)], ZeroFloor, refs, Options, false))
            .Should().Throw<ValidationException>();
        FluentActions.Invoking(() => QuoteApprovalPolicy.Evaluate(v, costs, [new(MarginFloorScope.Global, Guid.NewGuid(), null, 10m)], refs, Options, false))
            .Should().Throw<ValidationException>("a malformed floor row");
        QuoteApprovalPolicy.Evaluate(v, costs, ZeroFloor, refs, Options, false).Lines.Select(l => l.LineId)
            .Should().Equal(v.Lines.OrderBy(l => l.LineNo).Select(l => l.Id));
    }

    // ---- four-eyes and acknowledgement ----------------------------------------------------------------------------

    [Fact]
    public void Approve_ByTheAuthorSubmitterOrAContributor_IsRefused()
    {
        var quote = Drafted(); // created by Author
        quote.EditDraft(1, Content(Line(V1), Line(V2, qty: 4, price: 250_000)), Staff(Editor), Options, Now); // Editor contributes
        quote.Send(1, Issue(quote, overAuthority: true), Staff(Sender), false, Options, Now); // Sender submits
        var before = QuoteLifecycleTests.Fingerprint(quote.Version(1));

        foreach (var who in new[] { Author, Editor, Sender })
            FluentActions.Invoking(() => ApproveAs(quote, 1, Now.AddHours(1), approver: who))
                .Should().Throw<ConflictException>(who.ToString()).Which.Code.Should().Be("QUOTE_FOUR_EYES");
        FluentActions.Invoking(() => ApproveAs(quote, 1, Now.AddHours(1), approver: Guid.Empty)).Should().Throw<ValidationException>();
        QuoteLifecycleTests.Fingerprint(quote.Version(1)).Should().Be(before);

        ApproveAs(quote, 1, Now.AddHours(1));
        quote.Version(1).ApprovedBy.Should().Be(Approver);
    }

    [Fact]
    public void TheCreatorAndSubmitter_AreRefused_EvenIfTheContributorRowsWereNotLoaded()
    {
        var quote = Pending();
        // A version read without its contributor rows (a missing Include): the creator and submitter columns still decide.
        var contributors = (List<QuoteVersionContributor>)typeof(QuoteVersion)
            .GetField("_contributors", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(quote.Version(1))!;
        contributors.Clear();
        foreach (var who in new[] { Author, Sender })
            FluentActions.Invoking(() => ApproveAs(quote, 1, Now.AddHours(1), approver: who))
                .Should().Throw<ConflictException>(who.ToString()).Which.Code.Should().Be("QUOTE_FOUR_EYES");
    }

    [Fact]
    public void TheVersionsCreator_IsRefused_EvenWhenSomeoneElseSubmitted()
    {
        var quote = Sent();
        quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(1)); // Editor creates version 2
        quote.Send(2, Issue(quote, 2, overAuthority: true), Staff(Sender), false, Options, Now.AddHours(1));
        FluentActions.Invoking(() => ApproveAs(quote, 2, Now.AddHours(2), approver: Editor))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_FOUR_EYES");
        ApproveAs(quote, 2, Now.AddHours(2), approver: Author); // the author of version 1 did not touch version 2
        quote.Version(2).Status.Should().Be(QuoteVersionStatus.Sent);
    }

    [Fact]
    public void Approve_WithoutAcknowledgingExactlyTheFlaggedLines_IsRefused()
    {
        var quote = Drafted();
        var v = quote.Version(1);
        // Both lines 50 % below the buyer's price.
        QuoteApprovalCheck Both() => QuoteApprovalPolicy.Evaluate(v, [Cost(V1), Cost(V2)], ZeroFloor,
            v.Lines.Select(l => Reference(l.VariantId, l.UnitPriceMinor * 2)).ToList(), Options, false);
        SendWith(quote, Both()).Should().Be(QuoteVersionStatus.PendingApproval);
        var (l1, l2) = (LineId(quote), LineId(quote, 2));
        var hash = HashOf(quote);

        foreach (var (why, below, over) in new (string, Guid[], Guid[])[]
                 {
                     ("one of two", [], [l1]), ("none", [], []), ("an extra line", [], [l1, l2, Guid.NewGuid()]),
                     ("in the wrong list", [l1, l2], []), ("in both lists", [l1, l2], [l1, l2]),
                 })
            FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), new QuoteApproval(true, hash, below, over, null), Both(), false, Now.AddHours(1)))
                .Should().Throw<ConflictException>(why).Which.Code.Should().Be("QUOTE_FLAGS_NOT_ACKNOWLEDGED");
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.PendingApproval);

        quote.Approve(1, Staff(Approver), new QuoteApproval(false, hash, [], [l2, l1], null), Both(), false, Now.AddHours(1));
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Sent, "the order of the acknowledged ids does not matter");
    }

    [Fact]
    public void Approve_WithAStaleContentHash_IsRefused()
    {
        var quote = Pending();
        FluentActions.Invoking(() => ApproveAs(quote, 1, Now.AddHours(1), hash: Hash('f')))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");
        FluentActions.Invoking(() => ApproveAs(quote, 1, Now.AddHours(1), hash: HashOf(quote).ToUpperInvariant()))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");

        // The row was changed behind the domain's back (a bug or the database): the stored hash no longer matches the content.
        var stored = HashOf(quote);
        typeof(QuoteLine).GetProperty(nameof(QuoteLine.UnitPriceMinor))!.SetValue(quote.Version(1).Lines.First(), 1L);
        FluentActions.Invoking(() => ApproveAs(quote, 1, Now.AddHours(1), hash: stored))
            .Should().Throw<ConflictException>("the hash is recomputed, not only compared with the stored one").Which.Code.Should().Be("QUOTE_CHANGED");
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.PendingApproval);
    }

    [Fact]
    public void Accept_RecomputesTheHash_Too()
    {
        var quote = Sent();
        var stored = HashOf(quote);
        typeof(QuoteVersion).GetProperty(nameof(QuoteVersion.TermsText))!.SetValue(quote.Version(1), "Prices in USD.");
        FluentActions.Invoking(() => quote.Accept(1, stored, Guid.NewGuid(), Buyer, false, Now.AddHours(1)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");
    }

    [Fact]
    public void FlagsAreRecheckedAtApproval_WithTheCostFloorAndPriceOfThatMoment()
    {
        // Line 1 over authority at send; line 2 fine. Line 2 has a reason on file, in case it falls below the floor.
        var quote = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), CompanyA, null, Contact, Author, Options, Now);
        quote.EditDraft(1, Content(Line(V1), Line(V2, qty: 4, price: 250_000, reason: "Agreed with the owner for this project")), Staff(Author), Options, Now);
        var v = quote.Version(1);
        var (l1, l2) = (LineId(quote), LineId(quote, 2));
        QuoteApprovalCheck At(long v2Cost) => QuoteApprovalPolicy.Evaluate(v, [Cost(V1, 50_000), Cost(V2, v2Cost)], TenPercentFloor,
            [Reference(V1, 200_000), Reference(V2, 250_000)], Options, false);
        SendWith(quote, At(100_000)).Should().Be(QuoteVersionStatus.PendingApproval);
        v.Flags!.Should().Match<QuoteFlags>(f => f.BelowFloorLineIds.Count == 0 && f.OverAuthorityLineIds.SequenceEqual(new[] { l1 }));
        var hash = HashOf(quote);

        // The cost of V2 rose to 240,000 since: floor 264,000 > 250,000. The old acknowledgement is no longer enough.
        var now = At(240_000);
        now.Flags.BelowFloorLineIds.Should().Equal(l2);
        FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), new QuoteApproval(true, hash, [], [l1], null), now, false, Now.AddDays(1)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_FLAGS_NOT_ACKNOWLEDGED");
        FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), new QuoteApproval(true, hash, [l2], [l1], null), At(100_000), false, Now.AddDays(1)))
            .Should().Throw<ConflictException>("acknowledging against the stale check is acknowledging the wrong list")
            .Which.Code.Should().Be("QUOTE_FLAGS_NOT_ACKNOWLEDGED");
        FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), new QuoteApproval(false, hash, [l2], [l1], null), now, false, Now.AddDays(1)))
            .Should().Throw<ForbiddenException>().Which.Code.Should().Be("BELOW_FLOOR_PERMISSION_REQUIRED");
        // And now below cost: refused outright.
        FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), new QuoteApproval(true, hash, [l2], [l1], null), At(260_000), false, Now.AddDays(1)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_BELOW_COST");
        v.Status.Should().Be(QuoteVersionStatus.PendingApproval);
        v.Lines.Single(l => l.Id == l2).CostMinorAtCheck.Should().Be(100_000, "a refused approval records nothing");

        quote.Approve(1, Staff(Approver), new QuoteApproval(true, hash, [l2], [l1], "Cost rose; still worth it"), now, false, Now.AddDays(1));
        v.Status.Should().Be(QuoteVersionStatus.Sent);
        v.Flags!.BelowFloorLineIds.Should().Equal(l2);
        v.Lines.Single(l => l.Id == l2).Should().Match<QuoteLine>(l => l.CostMinorAtCheck == 240_000 && l.FloorMinorAtCheck == 264_000 && l.BelowFloor);
        v.ContentHash.Should().Be(hash, "check figures and flags are not in the hash");
    }

    [Fact]
    public void ALineThatFellBelowTheFloorWithoutAReason_CannotBeApproved_OnlyRejected()
    {
        var quote = Drafted();
        var v = quote.Version(1);
        QuoteApprovalCheck At(long v2Cost) => QuoteApprovalPolicy.Evaluate(v, [Cost(V1, 50_000), Cost(V2, v2Cost)], TenPercentFloor,
            [Reference(V1, 200_000), Reference(V2, 250_000)], Options, false);
        SendWith(quote, At(100_000));
        var now = At(240_000);
        FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), Ack(quote, now, belowFloorPermission: true), now, false, Now.AddDays(1)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("BELOW_FLOOR_REASON_REQUIRED");
        quote.RejectApproval(1, Approver, "Add a reason for line 2", Now.AddDays(1));
        v.Status.Should().Be(QuoteVersionStatus.Draft);
    }

    [Fact]
    public void Approve_BelowFloorWithoutTheExtraPermission_IsRefused()
    {
        var quote = OneLine(100_000, reason: "Strategic account, owner agreed");
        var check = Evaluate(quote, reference: 100_000, cost: 100_000, floors: TenPercentFloor);
        SendWith(quote, check).Should().Be(QuoteVersionStatus.PendingApproval);

        FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), Ack(quote, check), check, false, Now.AddHours(1)))
            .Should().Throw<ForbiddenException>().Which.Code.Should().Be("BELOW_FLOOR_PERMISSION_REQUIRED");
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.PendingApproval);
        quote.Approve(1, Staff(Approver), Ack(quote, check, belowFloorPermission: true), check, false, Now.AddHours(1));
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Sent);
    }

    [Fact]
    public void AnApprovalCheck_OfAnotherVersion_IsRefused()
    {
        var quote = Pending();
        var other = Pending();
        var check = Recheck(other);
        FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), Ack(quote, check), check, false, Now.AddHours(1)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");
        FluentActions.Invoking(() => quote.Approve(1, Staff(Approver), Ack(quote, check), null!, false, Now.AddHours(1)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");
    }

    // ---- conflict of interest (QT6, domain half) -------------------------------------------------------------------

    [Fact]
    public void AStaffMemberWhoBelongsToTheCompany_CannotEditApproveOrSendItsQuote()
    {
        var member = new QuoteActor(Editor, [CompanyB, CompanyA]);
        var quote = Drafted();
        var before = QuoteLifecycleTests.Fingerprint(quote.Version(1));
        FluentActions.Invoking(() => quote.EditDraft(1, Content(Line(V1, price: 1_000)), member, Options, Now))
            .Should().Throw<ForbiddenException>().Which.Code.Should().Be("QUOTE_CONFLICT_OF_INTEREST");
        FluentActions.Invoking(() => quote.Send(1, Issue(quote), member, false, Options, Now))
            .Should().Throw<ForbiddenException>().Which.Code.Should().Be("QUOTE_CONFLICT_OF_INTEREST");
        QuoteLifecycleTests.Fingerprint(quote.Version(1)).Should().Be(before);

        var pending = Pending();
        var check = Recheck(pending);
        FluentActions.Invoking(() => pending.Approve(1, new QuoteActor(Approver, [CompanyA]), Ack(pending, check), check, false, Now.AddHours(1)))
            .Should().Throw<ForbiddenException>().Which.Code.Should().Be("QUOTE_CONFLICT_OF_INTEREST");
        FluentActions.Invoking(() => pending.Approve(1, new QuoteActor(Approver, null!), Ack(pending, check), check, false, Now.AddHours(1)))
            .Should().Throw<ValidationException>("an unknown membership fails closed");
        pending.Version(1).Status.Should().Be(QuoteVersionStatus.PendingApproval);

        // A member of another company only is not conflicted.
        pending.Approve(1, new QuoteActor(Approver, [CompanyB]), Ack(pending, check), check, false, Now.AddHours(1));
        quote.EditDraft(1, Content(Line(V1)), new QuoteActor(Editor, [CompanyB]), Options, Now);
    }

    // ---- check figures -----------------------------------------------------------------------------------------------

    [Fact]
    public void Revise_StartsWithoutTheOldCheckFigures()
    {
        var quote = Pending();
        ApproveAs(quote, 1, Now.AddHours(1));
        var v2 = quote.Revise(Guid.NewGuid(), Editor, false, Now.AddHours(2));
        v2.Flags.Should().BeNull();
        v2.Lines.Should().AllSatisfy(l => (l.ReferenceUnitPriceMinor, l.ReferenceSource, l.CostMinorAtCheck, l.FloorMinorAtCheck, l.BelowFloor, l.OverAuthority)
            .Should().Be(((long?)null, (PriceSource?)null, (long?)null, (long?)null, false, false)));
        quote.Version(1).Lines.First().OverAuthority.Should().BeTrue("the issued version keeps its figures");
        typeof(QuoteLine).GetProperty(nameof(QuoteLine.ReferenceUnitPriceMinor), BindingFlags.Public | BindingFlags.Instance)!.SetMethod!.IsPrivate
            .Should().BeTrue("check figures are written only by the domain");
    }
}
