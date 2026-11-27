using System.Text.RegularExpressions;
using FluentAssertions;
using Mechware.Application.Invoicing;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Infrastructure.Invoicing;

namespace Mechware.UnitTests.Invoicing;

/// <summary>E6.7 pure parts (ADR-0012 §5, §13; threat model IT12, IT17): the Dhaka return month, the rate band, the CSV's formula guard, the role.</summary>
public class VatMonthExportTests
{
    private static DateTime Utc(string iso) => DateTime.SpecifyKind(DateTime.Parse(iso, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);

    /// <summary>Dhaka is UTC+6: the last 6 hours of a UTC month already belong to the next return month.</summary>
    [Theory]
    [InlineData("2026-11-30T17:59:59.9999999", "2026-11")]
    [InlineData("2026-11-30T18:00:00", "2026-12")]
    [InlineData("2026-11-30T23:30:00", "2026-12")]
    [InlineData("2026-11-01T00:00:00", "2026-11")]
    [InlineData("2026-10-31T17:59:59", "2026-10")]
    [InlineData("2026-12-31T18:00:00", "2027-01")]
    public void TheReturnMonth_IsTheDhakaCalendarMonth_NotTheUtcMonth(string utc, string month) =>
        VatMonth.Of(Utc(utc)).Label.Should().Be(month);

    [Fact]
    public void AMonth_IsTheUtcRangeFromDhakaMidnightToDhakaMidnight()
    {
        VatMonth.TryParse("2026-12", out var december).Should().BeTrue();
        (december.StartUtc, december.EndUtc).Should().Be((Utc("2026-11-30T18:00:00"), Utc("2026-12-31T18:00:00")));
        december.StartUtc.Kind.Should().Be(DateTimeKind.Utc);
        VatMonth.TryParse("2027-02", out var february).Should().BeTrue();
        (february.StartUtc, february.EndUtc).Should().Be((Utc("2027-01-31T18:00:00"), Utc("2027-02-28T18:00:00")));
        VatMonth.Of(december.StartUtc).Should().Be(december);
        VatMonth.Of(december.EndUtc.AddTicks(-1)).Should().Be(december);
        VatMonth.Of(december.EndUtc).Label.Should().Be("2027-01");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("2026-13")]
    [InlineData("2026-1")]
    [InlineData("26-12")]
    [InlineData("2026-12-01")]
    [InlineData("1999-12")]
    [InlineData("=2026-12")]
    public void AMonthThatIsNotYyyyMm_IsRefused(string? text) => VatMonth.TryParse(text, out _).Should().BeFalse();

    [Theory]
    [InlineData("0.15", "standard")]
    [InlineData("0.075", "reduced")]
    [InlineData("0.05", "reduced")]
    [InlineData("0", "zero_or_exempt")]
    [InlineData("0.2", "other")]
    public void TheRateBand_FollowsTheLineRate(string rate, string band) =>
        VatMonth.Band(decimal.Parse(rate, System.Globalization.CultureInfo.InvariantCulture)).Should().Be(band);

    /// <summary>IT17: buyer names and numbers are free text; a spreadsheet must never run one as a formula.</summary>
    [Fact]
    public void VatExport_NeutralisesFormulaPrefixes()
    {
        string[] evil = ["=HYPERLINK(\"http://x.test\",\"y\")", "+1+2", "-3+4", "@SUM(A1:A9)", "\t=1", "\r=2"];
        var sales = evil.Select((name, n) => new VatMonthSaleRow(Guid.NewGuid(), "MW-INV-2026-27-00000" + n, DateTime.UtcNow, "2026-12-01T10:00:00+06:00", "=ORDER",
            name, "1234567890123", 100, 0, 15, 115, "standard")).ToList();
        var credit = new VatMonthCreditNoteRow(Guid.NewGuid(), "MW-CN-2026-27-000001", DateTime.UtcNow, "2026-12-02T10:00:00+06:00", "MW-INV-2026-27-000001",
            "2026-11", "@evil", null, 10, 0, 1, 11, "standard");
        var report = new VatMonthReport("2026-12", DateTime.UtcNow, DateTime.UtcNow, sales, [credit], true,
            [new VatMonthVdsRow(Guid.NewGuid(), "VDS 2026/12", new DateOnly(2026, 12, 3), "2026-12-04T10:00:00+06:00", "MW-INV-2026-27-000001", "=VDS", null, 15)],
            [new VatMonthRateTotal("standard", 0.15m, 600, 0, 90, 10, 0, 1)],
            20_000_000, [new VatMonthMushak610Row("tax_invoice", "MW-INV-2026-27-000009", "x", "=BIG", null, 30_000_000)],
            [new VatMonthPaidNotShippedRow(Guid.NewGuid(), "+MW-1", "confirmed", "y", 500)], new VatMonthTotals(6, 600, 0, 90, 690, 1, 10, 0, 1, 11), false);

        var csv = VatMonthCsv.Write(report).Content;

        foreach (var name in evil) csv.Should().Contain("\"'" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"");
        csv.Should().Contain("\"'=ORDER\"").And.Contain("\"'@evil\"").And.Contain("\"'=BIG\"").And.Contain("\"'+MW-1\"").And.Contain("\"'=VDS\"");
        Regex.IsMatch(csv, "(^|,|\n)\"[=+@\t\r]", RegexOptions.Multiline).Should().BeFalse("no cell starts with a formula character");
        Regex.IsMatch(csv, "(^|,|\n)\"-[^0-9]", RegexOptions.Multiline).Should().BeFalse("a leading minus only ever starts a number");
        csv.Should().StartWith("﻿").And.Contain("\"1234567890123\"").And.Contain("consultant check before relying on it");
    }

    [Theory]
    [InlineData(Permissions.Roles.Finance, true)]
    [InlineData(Permissions.Roles.Admin, true)]
    [InlineData(Permissions.Roles.Support, false)]
    [InlineData(Permissions.Roles.Moderator, false)]
    [InlineData(Permissions.Roles.Buyer, false)]
    [InlineData(Permissions.Roles.Seller, false)]
    public void TheExportPermission_IsFinanceAndAdminOnly(string role, bool granted) =>
        Permissions.GetDefaultPermissionsForRole(role).Contains(Permissions.InvoicesExport).Should().Be(granted);
}
