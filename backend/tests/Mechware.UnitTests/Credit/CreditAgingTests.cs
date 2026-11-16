using FluentAssertions;
using Mechware.Domain.Credit;

namespace Mechware.UnitTests.Credit;

/// <summary>ADR-0010 §9 aging buckets (B3.8) and §10 CSV safety (threat model CS27).</summary>
public class CreditAgingTests
{
    private static readonly DateOnly Today = new(2026, 11, 17);

    /// <summary>The edges of every bucket: not yet due and due today are current; 1 and 30 are 1-30; 31 and 60 are 31-60; 61 and 90 are 61-90; 91 is 90+.</summary>
    [Theory]
    [InlineData(-5, "current")]
    [InlineData(-1, "current")]
    [InlineData(0, "current")]
    [InlineData(1, "1-30")]
    [InlineData(30, "1-30")]
    [InlineData(31, "31-60")]
    [InlineData(60, "31-60")]
    [InlineData(61, "61-90")]
    [InlineData(90, "61-90")]
    [InlineData(91, "90+")]
    [InlineData(400, "90+")]
    public void ADayCountFallsInExactlyOneBucket(int daysPastDue, string bucket)
    {
        var b = CreditAging.Of(Today.AddDays(-daysPastDue), 7_000, Today);
        var named = new Dictionary<string, long> { ["current"] = b.Current, ["1-30"] = b.Days1To30, ["31-60"] = b.Days31To60, ["61-90"] = b.Days61To90, ["90+"] = b.Days90Plus };
        named.Where(kv => kv.Value != 0).Select(kv => kv.Key).Should().Equal(bucket);
        b.Total.Should().Be(7_000);
        b.Overdue.Should().Be(bucket == "current" ? 0 : 7_000);
    }

    [Fact]
    public void BucketsAdd_AndNeverLoseAPoisha()
    {
        var sum = default(CreditAgingBuckets);
        long expected = 0;
        for (var days = -3; days <= 120; days++)
        {
            sum = sum.Plus(CreditAging.Of(Today.AddDays(-days), days + 1000, Today));
            expected += days + 1000;
        }
        sum.Total.Should().Be(expected);
    }

    [Theory]
    [InlineData("=HYPERLINK(\"http://x\")", "\"'=HYPERLINK(\"\"http://x\"\")\"")]
    [InlineData("+1+1", "\"'+1+1\"")]
    [InlineData("-2+3", "\"'-2+3\"")]
    [InlineData("@SUM(A1)", "\"'@SUM(A1)\"")]
    [InlineData("\tcmd", "\"'\tcmd\"")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("Acme, \"Traders\" Ltd", "\"Acme, \"\"Traders\"\" Ltd\"")]
    [InlineData("a=b", "\"a=b\"")]
    [InlineData("", "\"\"")]
    [InlineData(null, "\"\"")]
    public void CreditCsv_NeutralisesFormulaPrefixes(string? text, string cell) => CreditCsv.Text(text).Should().Be(cell);

    [Theory]
    [InlineData(0, "\"0\"", "\"0.00\"")]
    [InlineData(5, "\"5\"", "\"0.05\"")]
    [InlineData(123456, "\"123456\"", "\"1234.56\"")]
    [InlineData(-5, "\"-5\"", "\"-0.05\"")]
    [InlineData(-123400, "\"-123400\"", "\"-1234.00\"")]
    [InlineData(10_000_000_000, "\"10000000000\"", "\"100000000.00\"")]
    public void Amounts_AreWrittenTwice_AsPoishaAndAsBdt_AndStayNumbers(long poisha, string minor, string bdt)
    {
        CreditCsv.Minor(poisha).Should().Be(minor);
        CreditCsv.Bdt(poisha).Should().Be(bdt);
    }

    [Fact]
    public void ADocument_StartsWithTheByteOrderMark_AndEndsEveryRowWithCrLf() =>
        CreditCsv.Document([CreditCsv.Row(["\"a\"", "\"b\""]), CreditCsv.Row(["\"c\""])]).Should().Be("﻿\"a\",\"b\"\r\n\"c\"\r\n");
}
