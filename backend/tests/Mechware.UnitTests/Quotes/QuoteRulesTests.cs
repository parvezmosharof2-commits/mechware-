using FluentAssertions;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>D4.2: validity (ADR-0011 §7, QT5), numbers (§3, QT8), options (§15) and the line rules of §4.</summary>
public class QuoteRulesTests
{
    // ---- validity -----------------------------------------------------------------------------------------------------

    [Theory]
    // 10:00 Dhaka on 17 Nov, 15 days: last valid day 2 Dec, valid until 3 Dec 00:00 Dhaka = 2 Dec 18:00 UTC.
    [InlineData("2026-11-17T04:00:00Z", 15, "2026-12-02", "2026-12-02T18:00:00Z")]
    // 01:00 Dhaka on 18 Nov is still 17 Nov in UTC: the Dhaka date counts, so one day later than a UTC date would give.
    [InlineData("2026-11-17T19:00:00Z", 15, "2026-12-03", "2026-12-03T18:00:00Z")]
    // 23:59:59 Dhaka on 17 Nov.
    [InlineData("2026-11-17T17:59:59Z", 1, "2026-11-18", "2026-11-18T18:00:00Z")]
    // 00:00 Dhaka on 18 Nov.
    [InlineData("2026-11-17T18:00:00Z", 1, "2026-11-19", "2026-11-19T18:00:00Z")]
    // Across the year end, 90 days.
    [InlineData("2026-12-31T20:00:00Z", 90, "2027-04-01", "2027-04-01T18:00:00Z")]
    public void ValidUntil_IsTheStartOfTheDhakaDayAfterTheLastValidDay_AndIsExclusive(string sentAt, int days, string lastValid, string until)
    {
        var sent = DateTime.Parse(sentAt, null, System.Globalization.DateTimeStyles.AdjustToUniversal);
        var expected = DateTime.Parse(until, null, System.Globalization.DateTimeStyles.AdjustToUniversal);

        QuoteValidity.LastValidDhakaDate(sent, days).Should().Be(DateOnly.Parse(lastValid));
        var validUntil = QuoteValidity.ValidUntilUtc(sent, days);
        validUntil.Should().Be(expected);
        validUntil.Kind.Should().Be(DateTimeKind.Utc);
        QuoteValidity.IsValidAt(validUntil, validUntil.AddTicks(-1)).Should().BeTrue();
        QuoteValidity.IsValidAt(validUntil, validUntil).Should().BeFalse("valid_until is exclusive");
        QuoteValidity.IsValidAt(validUntil, validUntil.AddTicks(1)).Should().BeFalse();

        var quote = Drafted(sent.AddDays(-3));
        quote.EditDraft(1, Content(days, Line(V1)), Author, Options, sent.AddDays(-3));
        quote.Send(1, Issue(quote), Sender, false, Options, sent);
        quote.Version(1).ValidUntilUtc.Should().Be(expected);
        quote.IsAcceptable(1, false, expected.AddTicks(-1)).Should().BeTrue();
        quote.IsAcceptable(1, false, expected).Should().BeFalse();
    }

    [Fact]
    public void ValidityStartsWhenTheVersionIsSent_NotWhenItWasDrafted()
    {
        var drafted = Now;
        var direct = Drafted(drafted);
        direct.Send(1, Issue(direct), Sender, false, Options, drafted.AddDays(4));
        direct.Version(1).ValidUntilUtc.Should().Be(QuoteValidity.ValidUntilUtc(drafted.AddDays(4), 15));
        direct.Version(1).ValidUntilUtc.Should().NotBe(QuoteValidity.ValidUntilUtc(drafted, 15));

        // Through an approval: submitted on day 4, approved on day 6. The validity starts at the approval (= send).
        var approved = Drafted(drafted);
        approved.Send(1, Issue(approved, flags: OverAuthority(approved)), Sender, false, Options, drafted.AddDays(4));
        approved.Version(1).ValidUntilUtc.Should().BeNull();
        approved.Approve(1, Approver, Hash(), null, false, drafted.AddDays(6));
        approved.Version(1).ValidUntilUtc.Should().Be(QuoteValidity.ValidUntilUtc(drafted.AddDays(6), 15));
        approved.Version(1).SentAtUtc.Should().Be(drafted.AddDays(6));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(91)]
    [InlineData(366)]
    public void ValidityOutsideOneToTheMaximum_IsRefused(int days)
    {
        var quote = Drafted();
        FluentActions.Invoking(() => quote.EditDraft(1, Content(days, Line(V1)), Author, Options, Now))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainSingle(e => e.Field == "validityDays");
        quote.Version(1).ValidityDays.Should().Be(15);
    }

    [Fact]
    public void ValidityAllowedWhenDrafted_IsCheckedAgainAtSend_AgainstTheOptionsOfThatMoment()
    {
        var quote = Drafted();
        quote.EditDraft(1, Content(60, Line(V1)), Author, Options, Now);
        var tighter = Options with { MaxValidityDays = 45, ValidityWithoutApprovalDays = 30 };
        FluentActions.Invoking(() => quote.Send(1, Issue(quote), Sender, false, tighter, Now))
            .Should().Throw<ValidationException>();
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Draft);
    }

    [Fact]
    public void ANewQuote_UsesTheDefaultValidity() => Drafted().Version(1).ValidityDays.Should().Be(15);

    // ---- numbers ------------------------------------------------------------------------------------------------------

    [Fact]
    public void QuoteAndRfqNumbers_AreRandom_AndValidated()
    {
        var at = new DateTime(2026, 11, 17, 20, 0, 0, DateTimeKind.Utc); // the UTC date, as for orders
        var seq = new Queue<int>([10, 6, 16, 2, 22, 12]);
        QuoteNumber.NewQuote(at, _ => seq.Dequeue()).Should().Be("MWQ-261117-KGQCWM");
        QuoteNumber.NewRfq(at, _ => 31).Should().Be("MWR-261117-777777");

        // Every alphabet character can appear at every position (a seeded run); the default generator is the crypto RNG.
        var rng = new Random(4201);
        var seen = Enumerable.Range(0, 6).Select(_ => new HashSet<char>()).ToArray();
        for (var i = 0; i < 3_000; i++)
        {
            var n = QuoteNumber.NewQuote(at, rng.Next);
            QuoteNumber.IsQuoteNumber(n).Should().BeTrue(n);
            for (var p = 0; p < 6; p++) seen[p].Add(n[11 + p]);
        }

        seen.Should().AllSatisfy(s => s.Should().HaveCount(32));
        var real = Enumerable.Range(0, 300).Select(_ => QuoteNumber.NewQuote(at)).ToList();
        real.Should().OnlyHaveUniqueItems().And.AllSatisfy(n => QuoteNumber.IsQuoteNumber(n).Should().BeTrue());
        Enumerable.Range(0, 300).Select(_ => QuoteNumber.NewRfq(at)).Should().AllSatisfy(n => QuoteNumber.IsRfqNumber(n).Should().BeTrue());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("MWR-261117-K7Q2MX")] // an RFQ number is not a quote number
    [InlineData("MW-261117-K7Q2MX")] // an order number
    [InlineData("MWQ-261117-K7Q2M")]
    [InlineData("MWQ-261117-K7Q2MXA")]
    [InlineData("mwq-261117-K7Q2MX")]
    [InlineData("MWQ-261117-k7q2mx")]
    [InlineData("MWQ-261117-K7Q2M0")] // 0, 1, 8 and 9 are not base32
    [InlineData("MWQ-261117-K7Q2M8")]
    [InlineData("MWQ-261317-K7Q2MX")] // month 13
    [InlineData("MWQ-260230-K7Q2MX")] // 30 February
    [InlineData("MWQ-২৬১১১৭-K7Q2MX")]
    [InlineData("MWQ 261117 K7Q2MX")]
    [InlineData("MWQ-261117-K7Q2MX\n")]
    public void AMalformedQuoteNumber_IsRefused(string? number)
    {
        QuoteNumber.IsQuoteNumber(number).Should().BeFalse();
        if (number is not null)
            FluentActions.Invoking(() => Quote.Create(Guid.NewGuid(), Guid.NewGuid(), number, CompanyA, null, Contact, Author, Options, Now))
                .Should().Throw<ValidationException>();
    }

    [Fact]
    public void AWellFormedNumber_IsAccepted()
    {
        QuoteNumber.IsQuoteNumber("MWQ-261117-K7Q2MX").Should().BeTrue();
        QuoteNumber.IsRfqNumber("MWR-261117-K7Q2MX").Should().BeTrue();
        QuoteNumber.IsRfqNumber("MWQ-261117-K7Q2MX").Should().BeFalse();
        OrderNumber.IsValid("MWQ-261117-K7Q2MX").Should().BeFalse("a quote number is never taken for an order number");
    }

    // ---- options ------------------------------------------------------------------------------------------------------

    [Fact]
    public void DefaultOptions_AreTheOwnerDefaultsOfSection15()
    {
        var o = new QuoteOptions();
        (o.DefaultValidityDays, o.MaxValidityDays, o.ValidityWithoutApprovalDays, o.DiscountAuthorityBasisPoints,
            o.MaxRfqsPerCompanyPerHour, o.MaxOpenRfqsPerCompany).Should().Be((15, 90, 30, 500, 10, 20));
        o.Problems().Should().BeEmpty();
        o.Validated().Should().BeSameAs(o);
    }

    public static TheoryData<QuoteOptions, string> BadOptions => new()
    {
        { new QuoteOptions { DefaultValidityDays = 0 }, "DefaultValidityDays" },
        { new QuoteOptions { DefaultValidityDays = 91 }, "DefaultValidityDays" },
        { new QuoteOptions { MaxValidityDays = 366 }, "MaxValidityDays" },
        { new QuoteOptions { MaxValidityDays = 10 }, "DefaultValidityDays" },
        { new QuoteOptions { ValidityWithoutApprovalDays = 0 }, "ValidityWithoutApprovalDays" },
        { new QuoteOptions { ValidityWithoutApprovalDays = 91 }, "ValidityWithoutApprovalDays" },
        { new QuoteOptions { DiscountAuthorityBasisPoints = -1 }, "DiscountAuthorityBasisPoints" },
        { new QuoteOptions { DiscountAuthorityBasisPoints = 10_000 }, "DiscountAuthorityBasisPoints" },
        { new QuoteOptions { MaxRfqsPerCompanyPerHour = 0 }, "MaxRfqsPerCompanyPerHour" },
        { new QuoteOptions { MaxOpenRfqsPerCompany = 0 }, "MaxOpenRfqsPerCompany" },
    };

    [Theory]
    [MemberData(nameof(BadOptions))]
    public void BadOptions_StopTheHost(QuoteOptions options, string name)
    {
        options.Problems().Should().Contain(p => p.Contains(name, StringComparison.Ordinal));
        FluentActions.Invoking(options.Validated).Should().Throw<InvalidOperationException>().WithMessage($"*Quotes:{name}*");
    }

    // ---- line rules (§4) ----------------------------------------------------------------------------------------------

    [Fact]
    public void AVersion_HasAtMostTheOrderLineCap_SoItAlwaysFitsOneOrder()
    {
        QuoteLimits.MaxLinesPerVersion.Should().Be(PlaceOrderRequest.MaxLines);
        QuoteLimits.MaxQuantityPerLine.Should().Be(PlaceOrderRequest.MaxQuantityPerLine);
        var quote = Drafted();
        var fifty = Enumerable.Range(1, 50).Select(i => Line(Guid.NewGuid())).ToArray();
        quote.EditDraft(1, Content(fifty), Author, Options, Now);
        quote.Version(1).Lines.Select(l => l.LineNo).Should().Equal(Enumerable.Range(1, 50));

        var fiftyOne = fifty.Append(Line(Guid.NewGuid())).ToArray();
        FluentActions.Invoking(() => quote.EditDraft(1, Content(fiftyOne), Author, Options, Now)).Should().Throw<ValidationException>();
        quote.Version(1).Lines.Should().HaveCount(50);
    }

    public static TheoryData<string, QuoteLineDraft[]> BadLines => new()
    {
        { "duplicate variant", [Line(V1), Line(V1, qty: 20)] },
        { "quantity 0", [Line(V1, qty: 0)] },
        { "quantity above the order cap", [Line(V1, qty: 10_001)] },
        { "price 0", [Line(V1, price: 0)] },
        { "price above the maximum", [Line(V1, price: PriceListLimits.MaxUnitPriceMinor + 1)] },
        { "empty variant", [Line(Guid.Empty)] },
        { "line note too long", [Line(V1, note: new string('x', 301))] },
        { "below-floor reason too short", [Line(V1, reason: "low")] },
        { "facts of another variant", [Line(V1) with { Facts = Facts(V2) }] },
        { "VAT rate above 100 %", [Line(V1) with { Facts = Facts(V1, vat: 1.01m) }] },
        { "VAT rate with five decimals", [Line(V1) with { Facts = Facts(V1, vat: 0.07505m) }] },
        { "no public price", [Line(V1) with { Facts = Facts(V1, publicPrice: 0) }] },
        { "blank SKU", [Line(V1) with { Facts = Facts(V1) with { Sku = " " } }] },
        { "no seller", [Line(V1) with { Facts = Facts(V1) with { SellerId = Guid.Empty } }] },
    };

    [Theory]
    [MemberData(nameof(BadLines))]
    public void ABadLine_IsRefused_AndTheDraftKeepsItsLines(string why, QuoteLineDraft[] lines)
    {
        var quote = Drafted();
        var before = QuoteLifecycleTests.Fingerprint(quote.Version(1));
        FluentActions.Invoking(() => quote.EditDraft(1, Content(lines), Editor, Options, Now)).Should().Throw<ValidationException>(why);
        QuoteLifecycleTests.Fingerprint(quote.Version(1)).Should().Be(before);
    }

    [Fact]
    public void AQuantityThePackOrMinimumForbids_IsRefused_AndAnInactiveVariantIsUnavailable()
    {
        var quote = Drafted();
        FluentActions.Invoking(() => quote.EditDraft(1, Content(Line(V1) with { Facts = Facts(V1, problem: "SKU-1: sold in packs of 4.") }),
            Author, Options, Now)).Should().Throw<ValidationException>().WithMessage("*packs of 4*");
        FluentActions.Invoking(() => quote.EditDraft(1, Content(Line(V1) with { Facts = Facts(V1, active: false) }), Author, Options, Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("VARIANT_UNAVAILABLE");

        // The same rules again at send, with the facts of that moment.
        var issue = Issue(quote);
        var packChanged = issue with { Facts = [issue.Facts[0] with { QuantityProblem = "sold in packs of 3" }, issue.Facts[1]] };
        FluentActions.Invoking(() => quote.Send(1, packChanged, Sender, false, Options, Now)).Should().Throw<ValidationException>();
        var retired = issue with { Facts = [issue.Facts[0], issue.Facts[1] with { Active = false }] };
        FluentActions.Invoking(() => quote.Send(1, retired, Sender, false, Options, Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("VARIANT_UNAVAILABLE");
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Draft);
    }

    [Fact]
    public void Send_TakesTheSnapshotOfThatMoment_AndNeedsFactsForExactlyTheLines()
    {
        var quote = Drafted();
        var issue = Issue(quote);
        FluentActions.Invoking(() => quote.Send(1, issue with { Facts = [issue.Facts[0]] }, Sender, false, Options, Now))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("QUOTE_CHANGED");

        var fresh = issue with { Facts = [issue.Facts[0] with { TitleEn = "Siemens S7-1200 CPU 1214C", PublicUnitPriceMinor = 125_000 }, issue.Facts[1]] };
        quote.Send(1, fresh, Sender, false, Options, Now);
        quote.Version(1).Lines.First().Should().Match<QuoteLine>(l => l.TitleEn == "Siemens S7-1200 CPU 1214C" && l.PublicUnitPriceMinor == 125_000);
    }

    [Fact]
    public void Send_IsRefused_WithoutLinesHashSnapshotOrPreparer()
    {
        var empty = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), CompanyA, null, Contact, Author, Options, Now);
        FluentActions.Invoking(() => empty.Send(1, Issue(empty), Sender, false, Options, Now)).Should().Throw<ValidationException>();

        var quote = Drafted();
        foreach (var bad in new[]
                 {
                     Issue(quote, hash: "ABC"), Issue(quote, hash: new string('A', 64)), Issue(quote) with { PreparerName = " " },
                     Issue(quote) with { Company = Company with { Name = "" } },
                 })
            FluentActions.Invoking(() => quote.Send(1, bad, Sender, false, Options, Now)).Should().Throw<ValidationException>();
        quote.Version(1).Status.Should().Be(QuoteVersionStatus.Draft);
    }

    [Fact]
    public void ABelowFloorLine_NeedsAWrittenReason_AndFlagsNameOnlyLinesOfTheVersion()
    {
        var quote = Drafted();
        var line = quote.Version(1).Lines.First().Id;
        FluentActions.Invoking(() => quote.Send(1, Issue(quote, flags: new QuoteFlags([line], [], false)), Sender, false, Options, Now))
            .Should().Throw<ValidationException>().Which.Errors.Should().ContainSingle(e => e.Field == "belowFloorReason");
        FluentActions.Invoking(() => quote.Send(1, Issue(quote, flags: new QuoteFlags([], [Guid.NewGuid()], false)), Sender, false, Options, Now))
            .Should().Throw<ValidationException>();

        quote.EditDraft(1, Content(Line(V1, reason: "Project price agreed by the owner"), Line(V2)), Author, Options, Now);
        line = quote.Version(1).Lines.First().Id;
        quote.Send(1, Issue(quote, flags: new QuoteFlags([line], [], false)), Sender, false, Options, Now).Should().Be(QuoteVersionStatus.PendingApproval);
        quote.Version(1).Flags!.BelowFloorLineIds.Should().Equal(line);
    }

    [Fact]
    public void FixedDelivery_NeedsASiteAndOneFeePerSellerOfTheLines_AndCalculatedDeliveryHasNeither()
    {
        var quote = Drafted();
        var lines = new[] { Line(V1, seller: Seller1), Line(V2, seller: Seller2) };
        QuoteDraft Fixed(Guid? site, params QuoteShippingFee[] fees) =>
            Content(lines) with { ShippingMode = QuoteShippingMode.Fixed, ShipToSiteId = site, ShippingFees = fees };

        foreach (var bad in new[]
                 {
                     Fixed(null, new(Seller1, 5_000), new(Seller2, 0)), Fixed(Site), Fixed(Site, new(Seller1, -1), new(Seller2, 0)),
                     Fixed(Site, new(Seller1, 5_000), new(Seller1, 6_000)),
                     Content(lines) with { ShipToSiteId = Site }, Content(lines) with { ShippingFees = [new(Seller1, 5_000)] },
                 })
            FluentActions.Invoking(() => quote.EditDraft(1, bad, Author, Options, Now)).Should().Throw<ValidationException>();

        quote.EditDraft(1, Fixed(Site, new QuoteShippingFee(Seller1, 5_000)), Author, Options, Now); // a draft may still be incomplete
        FluentActions.Invoking(() => quote.Send(1, Issue(quote), Sender, false, Options, Now))
            .Should().Throw<ValidationException>("every seller of the lines needs its fee").Which.Errors.Should().ContainSingle(e => e.Field == "shippingFees");

        quote.EditDraft(1, Fixed(Site, new(Seller1, 5_000), new(Seller2, 0)), Author, Options, Now);
        quote.Send(1, Issue(quote), Sender, false, Options, Now).Should().Be(QuoteVersionStatus.Sent);
        quote.Version(1).ShippingFees!.Select(f => f.ShippingMinor).Should().Equal(5_000, 0);
    }

    [Fact]
    public void FreeText_IsTrimmed_AndLimited()
    {
        var quote = Drafted();
        quote.EditDraft(1, Content(Line(V1)) with { BuyerNote = "  ", TermsText = " Net 30 ", InternalNote = null }, Author, Options, Now);
        (quote.Version(1).BuyerNote, quote.Version(1).TermsText, quote.Version(1).InternalNote).Should().Be((null, "Net 30", null));

        foreach (var bad in new[]
                 {
                     Content(Line(V1)) with { TermsText = new string('t', 4_001) }, Content(Line(V1)) with { BuyerNote = new string('b', 2_001) },
                     Content(Line(V1)) with { InternalNote = new string('i', 2_001) }, Content(Line(V1)) with { PaymentTerms = (QuotePaymentTerms)9 },
                 })
            FluentActions.Invoking(() => quote.EditDraft(1, bad, Author, Options, Now)).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Create_NeedsACompanyContactAndAuthor()
    {
        foreach (var (company, contact, author) in new[] { (Guid.Empty, Contact, Author), (CompanyA, Guid.Empty, Author), (CompanyA, Contact, Guid.Empty) })
            FluentActions.Invoking(() => Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), company, null, contact, author, Options, Now))
                .Should().Throw<ValidationException>();

        var rfq = Guid.NewGuid();
        var quote = Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), CompanyA, rfq, Contact, Author, Options, Now);
        (quote.CompanyId, quote.RfqId, quote.ContactUserId, quote.CreatedBy, quote.LatestVersionNo).Should().Be((CompanyA, (Guid?)rfq, Contact, Author, 1));
        quote.Version(1).Should().Match<QuoteVersion>(v => v.Status == QuoteVersionStatus.Draft && v.CreatedBy == Author && v.Lines.Count == 0);
    }
}
