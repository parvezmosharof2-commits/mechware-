using FluentAssertions;
using Mechware.Domain.Companies;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>D4.5, QT17 (domain half): free text of an RFQ is plain text with control characters removed, before it is trimmed or measured.</summary>
public class PlainTextTests
{
    [Theory]
    [InlineData("a\u0000b", "ab")]
    [InlineData("a\u0007b\u001bc", "abc")]
    [InlineData("line1\r\nline2", "line1\nline2")]
    [InlineData("tab\there", "tab\there")]
    [InlineData("a​b﻿c", "abc")]
    [InlineData("pay ‮gnp.exe", "pay gnp.exe")]
    [InlineData("⁦x⁩", "x")]
    [InlineData("a\u0085b\u007Fc", "abc")]
    public void ControlAndInvisibleFormatCharacters_AreRemoved_NewlineAndTabStay(string raw, string expected) =>
        PlainText.Clean(raw).Should().Be(expected);

    [Fact]
    public void BanglaText_KeepsItsJoiners_AndIsNormalised()
    {
        const string bangla = "র‍্য কারখানা ‌";
        PlainText.Clean(bangla).Should().Be(bangla, "the zero-width joiner and non-joiner change how Bangla is spelt");
        PlainText.Clean("é").Should().Be("é", "NFC, as the quick-order parser does");
    }

    [Fact]
    public void ALoneSurrogate_BecomesTheReplacementCharacter_InsteadOfThrowing() =>
        PlainText.Clean("a\uD800b").Should().Be("a�b");

    [Fact]
    public void Null_StaysNull() => PlainText.Clean(null).Should().BeNull();

    [Fact]
    public void MarkupIsNotTouched_ItIsStoredAsText_AndNeverInterpreted() =>
        PlainText.Clean("<script>alert(1)</script> =HYPERLINK(\"x\")").Should().Be("<script>alert(1)</script> =HYPERLINK(\"x\")");

    [Fact]
    public void TheRfq_StoresControlFreeText_AndMeasuresTheLengthAfterCleaning()
    {
        var padded = new string('m', 1_990) + new string('\u0001', 50);   // 2,040 characters raw, 1,990 once cleaned
        var rfq = Rfq.Submit(Guid.NewGuid(), QuoteNumber.NewRfq(Now), CompanyA, Buyer, RfqSource.Text, "  need\u0000ed‮ soon  ", null, null,
            [new RfqLineInput("LC1D09\u0000 x5", "LC1D09\u0008", 5, RfqMatchStatus.NotFound, null, null, "n\u0001ote")], "rfq-key-0001", Hash('1'), Now);
        rfq.Message.Should().Be("needed soon");
        rfq.Lines.Single().Should().Match<RfqLine>(l => l.RawText == "LC1D09 x5" && l.PartKey == "LC1D09" && l.Note == "note");

        Rfq.Submit(Guid.NewGuid(), QuoteNumber.NewRfq(Now), CompanyA, Buyer, RfqSource.Text, padded, null, null,
            [new RfqLineInput("x", null, 1, RfqMatchStatus.NotFound, null, null, null)], "rfq-key-0002", Hash('2'), Now).Message.Should().HaveLength(1_990);
    }

    [Fact]
    public void WhoSeesAllOfACompanysRfqs_DefaultsToAdminAndBuyer_AndMustNameAKnownRole()
    {
        new QuoteOptions().VisibleTo.Should().Be(CompanyRoles.Admin | CompanyRoles.Buyer);
        new QuoteOptions().Problems().Should().BeEmpty();
        (new QuoteOptions { VisibleTo = CompanyRoles.None }).Problems().Should().ContainSingle().Which.Should().Contain("VisibleTo");
        (new QuoteOptions { VisibleTo = (CompanyRoles)64 }).Problems().Should().ContainSingle().Which.Should().Contain("VisibleTo");
    }
}
