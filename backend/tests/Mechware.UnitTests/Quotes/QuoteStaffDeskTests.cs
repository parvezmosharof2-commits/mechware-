using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Quotes;
using static Mechware.UnitTests.Quotes.QuoteBuilders;

namespace Mechware.UnitTests.Quotes;

/// <summary>
/// D4.6 domain: the SLA clock of the inbox (ADR-0011 §15 decision 12: Sunday-Thursday 09:00-18:00 Dhaka), the conflict-of-interest
/// refusal on create and revise (§5, QT6), and revise copying the highest version whatever order the versions were loaded in.
/// <see cref="QuoteBuilders.Now"/> is Tuesday 17 November 2026, 10:00 in Dhaka.
/// </summary>
public class QuoteStaffDeskTests
{
    private static readonly DateOnly[] NoHolidays = [];

    /// <summary>A Dhaka wall-clock time in November 2026 as UTC.</summary>
    private static DateTime Dhaka(int day, int hour, int minute = 0) => new DateTime(2026, 11, day, hour, minute, 0, DateTimeKind.Utc).AddHours(-6);

    // ---- SLA clock ----------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(17, 10, 0, 17, 14, 0)]   // Tuesday morning: the same afternoon
    [InlineData(17, 14, 0, 17, 18, 0)]   // exactly fills the day: due at closing time, not the next morning
    [InlineData(17, 16, 0, 18, 11, 0)]   // two hours today, two tomorrow
    [InlineData(17, 7, 0, 17, 13, 0)]    // before opening: the clock starts at 09:00
    [InlineData(17, 18, 0, 18, 13, 0)]   // at closing time: nothing counts until tomorrow 09:00
    [InlineData(19, 17, 0, 22, 12, 0)]   // Thursday 17:00: one hour, then Friday and Saturday are skipped, Sunday 09:00 + 3 h
    [InlineData(20, 10, 0, 22, 13, 0)]   // asked on a Friday: Sunday 09:00 + 4 h
    [InlineData(21, 23, 30, 22, 13, 0)]  // Saturday night
    [InlineData(22, 3, 0, 22, 13, 0)]    // Sunday 03:00 Dhaka is Saturday 21:00 UTC: the Dhaka day decides, not the UTC one
    public void DueAt_CountsOnlySundayToThursday_0900To1800_InDhaka(int day, int hour, int minute, int dueDay, int dueHour, int dueMinute)
    {
        var due = QuoteSla.DueAtUtc(Dhaka(day, hour, minute), 4 * 60, NoHolidays);
        due.Should().Be(Dhaka(dueDay, dueHour, dueMinute));
        due.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void AHoliday_IsSkippedLikeAWeekend()
    {
        DateOnly[] holidays = [new(2026, 11, 18)];
        QuoteSla.DueAtUtc(Dhaka(17, 16), 240, holidays).Should().Be(Dhaka(19, 11));
        QuoteSla.WorkingMinutes(Dhaka(17, 16), Dhaka(19, 11), holidays).Should().Be(240);
        QuoteSla.IsWorkingDay(new DateOnly(2026, 11, 18), holidays).Should().BeFalse();
        QuoteSla.IsWorkingDay(new DateOnly(2026, 11, 20), NoHolidays).Should().BeFalse("Friday");
        QuoteSla.IsWorkingDay(new DateOnly(2026, 11, 21), NoHolidays).Should().BeFalse("Saturday");
        QuoteSla.IsWorkingDay(new DateOnly(2026, 11, 22), NoHolidays).Should().BeTrue("Sunday is a working day in Bangladesh");
    }

    [Fact]
    public void WorkingMinutes_CountOnlyOpeningHours()
    {
        QuoteSla.WorkingMinutes(Dhaka(17, 16), Dhaka(18, 11), NoHolidays).Should().Be(240);
        QuoteSla.WorkingMinutes(Dhaka(19, 17), Dhaka(22, 12), NoHolidays).Should().Be(240, "Friday and Saturday do not count");
        QuoteSla.WorkingMinutes(Dhaka(17, 7), Dhaka(17, 8, 59), NoHolidays).Should().Be(0, "before opening");
        QuoteSla.WorkingMinutes(Dhaka(17, 12), Dhaka(17, 11), NoHolidays).Should().Be(0, "an end before the start");
        QuoteSla.WorkingMinutes(Dhaka(15, 9), Dhaka(19, 18), NoHolidays).Should().Be(5 * 9 * 60, "a whole working week has 45 hours");
    }

    [Fact]
    public void WorkingMinutesUntilTheDueTime_AreExactlyTheBudget_ForRandomStarts()
    {
        var random = new Random(4606);
        DateOnly[] holidays = [new(2026, 12, 16), new(2026, 12, 25)];
        for (var i = 0; i < 2_000; i++)
        {
            var start = Dhaka(1, 0).AddMinutes(random.Next(0, 60 * 24 * 60));
            var budget = random.Next(0, 60 * 50);
            var due = QuoteSla.DueAtUtc(start, budget, holidays);
            QuoteSla.WorkingMinutes(start, due, holidays).Should().Be(budget, $"start {start:O}, {budget} min");
            due.Should().BeOnOrAfter(start);
        }
    }

    [Fact]
    public void SlaOptions_AreValidated_AndHolidaysBindFromConfiguration()
    {
        new QuoteOptions().FirstQuoteSlaWorkingHours.Should().Be(4, "owner decision: first quote within 4 working hours");
        new QuoteOptions().SlaHolidays.Should().BeEmpty("owner item c: no holiday list yet");
        foreach (var bad in new[] { 0, 46 })
            FluentActions.Invoking(() => (new QuoteOptions() with { FirstQuoteSlaWorkingHours = bad }).Validated())
                .Should().Throw<InvalidOperationException>().WithMessage("*FirstQuoteSlaWorkingHours*");

        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Quotes:FirstQuoteSlaWorkingHours"] = "6",
            ["Quotes:SlaHolidays:0"] = "2026-12-16",
            ["Quotes:SlaHolidays:1"] = "2026-03-26",
        }).Build();
        var bound = config.GetSection(QuoteOptions.Section).Get<QuoteOptions>()!.Validated();
        bound.FirstQuoteSlaWorkingHours.Should().Be(6);
        bound.SlaHolidays.Should().Equal(new DateOnly(2026, 12, 16), new DateOnly(2026, 3, 26));
    }

    // ---- conflict of interest on create and revise (QT6) --------------------------------------------------------------

    [Fact]
    public void AStaffMemberOfTheCompany_CannotCreateItsQuote()
    {
        var member = new QuoteActor(Author, [CompanyB, CompanyA]);
        var act = () => Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), CompanyA, null, Contact, member, Options, Now);
        act.Should().Throw<ForbiddenException>().Which.Code.Should().Be("QUOTE_CONFLICT_OF_INTEREST");

        Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), CompanyA, null, Contact, new QuoteActor(Author, [CompanyB]), Options, Now)
            .CreatedBy.Should().Be(Author, "a member of another company is no conflict");
        FluentActions.Invoking(() => Quote.Create(Guid.NewGuid(), Guid.NewGuid(), QuoteNumber.NewQuote(Now), CompanyA, null, Contact,
            new QuoteActor(Author, null!), Options, Now)).Should().Throw<ValidationException>("unknown memberships fail closed");
    }

    [Fact]
    public void AStaffMemberOfTheCompany_CannotReviseItsQuote_AndNothingChanges()
    {
        var quote = Sent();
        var act = () => quote.Revise(Guid.NewGuid(), new QuoteActor(Editor, [CompanyA]), false, Now.AddHours(1));
        act.Should().Throw<ForbiddenException>().Which.Code.Should().Be("QUOTE_CONFLICT_OF_INTEREST");
        (quote.Versions.Count, quote.LatestVersionNo).Should().Be((1, 1));
    }

    [Fact]
    public void Revise_CopiesTheHighestVersion_EvenWhenTheVersionsWereLoadedOutOfOrder()
    {
        var quote = Sent();
        quote.Revise(Guid.NewGuid(), Staff(Editor), false, Now.AddHours(1));
        quote.EditDraft(2, Content(Line(V3, qty: 7, price: 99_000)), Staff(Editor), Options, Now.AddHours(1));
        quote.Send(2, Issue(quote, 2), Staff(Sender), false, Options, Now.AddHours(2));

        // A database returns rows in any order unless asked: put version 2 first, as an unordered Include may.
        var versions = (List<QuoteVersion>)typeof(Quote).GetField("_versions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(quote)!;
        versions.Reverse();
        versions[^1].VersionNo.Should().Be(1, "the precondition: the last loaded version is not the latest");

        var v3 = quote.Revise(Guid.NewGuid(), Staff(Editor), false, Now.AddHours(3));
        v3.VersionNo.Should().Be(3);
        v3.Lines.Select(l => (l.VariantId, l.Quantity, l.UnitPriceMinor)).Should().Equal((V3, 7, 99_000L));
    }
}
