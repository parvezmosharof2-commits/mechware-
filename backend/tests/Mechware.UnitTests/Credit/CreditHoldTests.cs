using FluentAssertions;
using Mechware.Application.Credit;
using Mechware.Domain.Common.Enums;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.ValueObjects;
using Mechware.Domain.Companies;

namespace Mechware.UnitTests.Credit;

/// <summary>ADR-0010 §9 (B3.7, threat model CS25): the hold date, the hold itself, an Admin's lift and the sweep's note.</summary>
public class CreditHoldTests
{
    private static readonly DateTime Now = new(2026, 11, 17, 18, 30, 0, DateTimeKind.Utc);   // 00:30 on the 18th in Dhaka

    private static Company NewCompany()
    {
        var c = Company.Register(Guid.NewGuid(), "Overdue Traders Ltd", "TL-B37-1", "0012345678901", null, null);
        c.Activate();
        c.SetCredit(CreditTerms.Net30, new Money(100_000, Currency.BDT));
        return c;
    }

    [Theory]
    [InlineData(15, "2026-11-03")]   // due on the 2nd: 16 days past due on the 18th (held); due on the 3rd: exactly 15 (not held)
    [InlineData(1, "2026-11-17")]
    [InlineData(365, "2025-11-18")]
    public void HoldDueBefore_IsTheDhakaDate_MinusTheHoldDays(int days, string expected) =>
        new CreditOptions { HoldAfterDaysPastDue = days }.HoldDueBefore(Now).Should().Be(DateOnly.Parse(expected));

    [Fact]
    public void HoldDueBefore_IsNull_WhenTheOwnerChoseFlagOnly() =>
        new CreditOptions { HoldAfterDaysPastDue = 0 }.HoldDueBefore(Now).Should().BeNull();

    [Fact]
    public void AnOverdueCompany_IsOnHold_AndAnotherCompanyIsNot()
    {
        var c = NewCompany();
        c.Invoking(x => x.EnsureNoCreditHold(null, Now)).Should().NotThrow();
        c.Invoking(x => x.EnsureNoCreditHold(new DateOnly(2026, 11, 2), Now)).Should().Throw<ConflictException>()
            .Which.Code.Should().Be("CREDIT_HOLD_OVERDUE");
    }

    [Fact]
    public void ALift_HoldsForItsDaysOnly_AndZeroEndsIt()
    {
        var c = NewCompany();
        var overdue = new DateOnly(2026, 10, 1);
        c.LiftCreditHold(7, 7, "  customer promised a cheque on Thursday  ", Now).Should().Be("customer promised a cheque on Thursday");
        c.CreditHoldOverrideUntilUtc.Should().Be(Now.AddDays(7));
        c.Invoking(x => x.EnsureNoCreditHold(overdue, Now.AddDays(7).AddSeconds(-1))).Should().NotThrow();
        c.Invoking(x => x.EnsureNoCreditHold(overdue, Now.AddDays(7))).Should().Throw<ConflictException>("the lift has expired");

        c.LiftCreditHold(0, 7, "cheque bounced again", Now.AddDays(1));
        c.CreditHoldOverrideUntilUtc.Should().BeNull();
        c.Invoking(x => x.EnsureNoCreditHold(overdue, Now.AddDays(1))).Should().Throw<ConflictException>();
    }

    [Theory]
    [InlineData(8, 7, "a fair reason", "days")]
    [InlineData(-1, 7, "a fair reason", "days")]
    [InlineData(4, 3, "a fair reason", "days")]
    [InlineData(3, 7, "shrt", "reason")]
    [InlineData(3, 7, null, "reason")]
    public void ALift_BeyondTheMaximum_OrWithoutAReason_IsRefused(int days, int max, string? reason, string field)
    {
        var c = NewCompany();
        c.Invoking(x => x.LiftCreditHold(days, max, reason, Now)).Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainSingle(e => e.Field == field);
        c.CreditHoldOverrideUntilUtc.Should().BeNull("a refused lift changes nothing");
    }

    [Fact]
    public void TheSweepNote_ChangesOnlyWhenTheHoldStartsOrEnds()
    {
        var c = NewCompany();
        c.NoteCreditHold(false, Now).Should().BeFalse();
        c.NoteCreditHold(true, Now).Should().BeTrue();
        c.NoteCreditHold(true, Now.AddHours(1)).Should().BeFalse();
        c.CreditHoldSinceUtc.Should().Be(Now, "the first pass that saw it");
        c.NoteCreditHold(false, Now.AddDays(1)).Should().BeTrue();
        c.CreditHoldSinceUtc.Should().BeNull();
    }
}
