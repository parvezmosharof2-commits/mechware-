using FluentAssertions;
using Mechware.Domain.Invoicing;

namespace Mechware.UnitTests.Invoicing;

/// <summary>ADR-0012 §4–§5, threat model §১৪ IT10 and IT11 (the pure part): fiscal year, number format, the counter's step.</summary>
public class InvoiceNumberingTests
{
    private static DateTime Utc(string iso) => DateTime.SpecifyKind(DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    [Theory]
    [InlineData("2026-06-30T17:59:59.9999999", 2025)] // 30 June 23:59:59 Dhaka: still the old year
    [InlineData("2026-06-30T18:00:00", 2026)]         // 1 July 00:00 Dhaka: the new year, although it is 30 June in UTC
    [InlineData("2026-06-30T23:59:59", 2026)]
    [InlineData("2026-07-01T00:00:00", 2026)]
    [InlineData("2027-01-01T00:00:00", 2026)]
    [InlineData("2026-12-31T18:00:00", 2026)]         // 1 January Dhaka: calendar year changes, fiscal year does not
    [InlineData("2027-06-30T17:59:59", 2026)]
    public void FiscalYear_IsTheJulyJuneYearOfTheDhakaDate(string utc, int expected) =>
        FiscalYear.Of(Utc(utc)).Should().Be(expected);

    /// <summary>Property: for any instant, the year follows the +06:00 calendar (oracle: DateTimeOffset), not the UTC one.</summary>
    [Fact]
    public void FiscalYear_UsesTheDhakaDate_NotUtc()
    {
        var random = new Random(20261121);
        var differsFromUtc = 0;
        for (var i = 0; i < 20_000; i++)
        {
            // Every fourth instant falls in the six hours where the UTC and the Dhaka dates differ around the 30 June edge.
            var utc = i % 4 == 0
                ? new DateTime(random.Next(2000, 2098), 6, 30, 18, 0, 0, DateTimeKind.Utc).AddTicks(random.NextInt64(TimeSpan.TicksPerHour * 6))
                : new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddTicks(random.NextInt64(TimeSpan.TicksPerDay * 365 * 98));
            var dhaka = new DateTimeOffset(utc).ToOffset(TimeSpan.FromHours(6));
            var oracle = dhaka.Month >= 7 ? dhaka.Year : dhaka.Year - 1;
            FiscalYear.Of(utc).Should().Be(oracle, "instant {0:O}", utc);
            if ((utc.Month >= 7 ? utc.Year : utc.Year - 1) != oracle) differsFromUtc++;
        }
        differsFromUtc.Should().BeGreaterThan(4000, "the sample covers the window where a UTC year would be wrong");
    }

    [Fact]
    public void FiscalYear_RefusesALocalTime() =>
        FluentActions.Invoking(() => FiscalYear.Of(DateTime.SpecifyKind(DateTime.UtcNow, DateTimeKind.Local))).Should().Throw<ArgumentException>();

    [Theory]
    [InlineData(2026, "2026-27")]
    [InlineData(2099, "2099-00")]
    [InlineData(2009, "2009-10")]
    public void FiscalYear_Label_IsStartYearDashTwoDigits(int year, string label) => FiscalYear.Label(year).Should().Be(label);

    [Theory]
    [InlineData(InvoiceSeries.TaxInvoice, 2026, 482, "MW-INV-2026-27-000482")]
    [InlineData(InvoiceSeries.CreditNote, 2026, 17, "MW-CN-2026-27-000017")]
    [InlineData(InvoiceSeries.SpecimenTaxInvoice, 2026, 1, "SPEC-INV-2026-27-000001")]
    [InlineData(InvoiceSeries.SpecimenCreditNote, 2025, 3, "SPEC-CN-2025-26-000003")]
    [InlineData(InvoiceSeries.TaxInvoice, 2026, 1_234_567, "MW-INV-2026-27-1234567")]
    public void InvoiceNumber_Format_FollowsTheAdr(InvoiceSeries series, int year, long serial, string expected) =>
        InvoiceNumber.Format("MW", series, year, serial).Should().Be(expected);

    [Theory]
    [InlineData("mw", 1L, 2026)]
    [InlineData("SPEC", 1L, 2026)]
    [InlineData("M", 1L, 2026)]
    [InlineData("MW/X", 1L, 2026)]
    [InlineData("MW", 0L, 2026)]
    [InlineData("MW", 1L, 0)]
    public void InvoiceNumber_Format_RefusesABadPrefixSerialOrYear(string prefix, long serial, int year) =>
        FluentActions.Invoking(() => InvoiceNumber.Format(prefix, InvoiceSeries.TaxInvoice, year, serial)).Should().Throw<ArgumentException>();

    [Fact]
    public void Counter_FirstIssue_StartsAtOne_ThenStepsByOne()
    {
        var c = new InvoiceNumberCounter(Guid.NewGuid(), InvoiceSeries.TaxInvoice);
        c.Take(Utc("2026-08-01T10:00:00")).Should().Be(((int, long)?)(2026, 1L));
        c.Take(Utc("2026-08-01T10:00:01")).Should().Be(((int, long)?)(2026, 2L));
        c.Take(Utc("2026-08-01T10:00:01")).Should().Be(((int, long)?)(2026, 3L), "two issues in the same microsecond are not a clock going back");
        (c.Year, c.NextSerial, c.LastIssuedAtUtc).Should().Be((2026, 4L, (DateTime?)Utc("2026-08-01T10:00:01")));
    }

    [Fact]
    public void AnIssueAtTheLastSecondOfJune30Dhaka_IsInTheOldYear_AndTheNextSecondStartsAtOne()
    {
        var c = new InvoiceNumberCounter(Guid.NewGuid(), InvoiceSeries.TaxInvoice, 2025, 482, Utc("2026-06-30T10:00:00"));
        c.Take(Utc("2026-06-30T17:59:59")).Should().Be(((int, long)?)(2025, 482L));
        c.Take(Utc("2026-06-30T18:00:00")).Should().Be(((int, long)?)(2026, 1L));
        c.Take(Utc("2026-06-30T18:00:01")).Should().Be(((int, long)?)(2026, 2L));
    }

    [Theory]
    [InlineData("2026-08-01T09:59:59")]  // earlier instant, same year
    [InlineData("2026-06-30T17:00:00")]  // earlier year
    public void Counter_ClockBehindTheLastIssue_IsRefused_AndChangesNothing(string utc)
    {
        var last = Utc("2026-08-01T10:00:00");
        var c = new InvoiceNumberCounter(Guid.NewGuid(), InvoiceSeries.CreditNote, 2026, 9, last);
        c.Take(Utc(utc)).Should().BeNull();
        (c.Year, c.NextSerial, c.LastIssuedAtUtc).Should().Be((2026, 9L, (DateTime?)last));
    }
}
