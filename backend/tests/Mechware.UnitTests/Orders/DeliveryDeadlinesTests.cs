using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Orders;
using Xunit;

namespace Mechware.UnitTests.Orders;

/// <summary>E8.3: the legal delivery periods are configuration; the pure rule that turns them into instants.</summary>
public class DeliveryDeadlinesTests
{
    private static readonly DateTime T = new(2026, 3, 1, 10, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void SameCity_gets_five_days_and_other_city_ten_from_the_confirmation_instant()
    {
        var same = DeliveryDeadlines.Compute(T, true, 5, 10, 48);
        var other = DeliveryDeadlines.Compute(T, false, 5, 10, 48);

        same.LegalDeliveryDeadlineUtc.Should().Be(T.AddDays(5));
        other.LegalDeliveryDeadlineUtc.Should().Be(T.AddDays(10));
        same.CourierHandoverDeadlineUtc.Should().Be(T.AddHours(48));
        other.CourierHandoverDeadlineUtc.Should().Be(T.AddHours(48));
    }

    [Fact]
    public void An_unknown_district_fails_strict_with_the_short_period()
    {
        var unknown = DeliveryDeadlines.Compute(T, null, 5, 10, 48);
        unknown.LegalDeliveryDeadlineUtc.Should().Be(T.AddDays(5));
        unknown.SameCity.Should().BeNull();
    }

    [Theory]
    [InlineData(0, 10, 48)]
    [InlineData(5, 0, 48)]
    [InlineData(5, 10, 0)]
    [InlineData(-1, 10, 48)]
    public void Non_positive_periods_are_refused(int same, int other, int hours)
    {
        var act = () => DeliveryDeadlines.Compute(T, true, same, other, hours);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }

    private static DeliveryOptions Read(params (string Key, string? Value)[] values) =>
        DeliveryOptions.From(new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => $"Delivery:{v.Key}", v => v.Value)).Build());

    [Fact]
    public void Defaults_are_5_10_48_and_configuration_overrides_them()
    {
        var d = Read();
        (d.SameCityDays, d.OtherCityDays, d.HandoverHours).Should().Be((5, 10, 48));

        var c = Read(("SameCityDays", "3"), ("OtherCityDays", "7"), ("HandoverHours", "24"));
        c.Compute(T, true).LegalDeliveryDeadlineUtc.Should().Be(T.AddDays(3));
        c.Compute(T, false).LegalDeliveryDeadlineUtc.Should().Be(T.AddDays(7));
        c.Compute(T, false).CourierHandoverDeadlineUtc.Should().Be(T.AddHours(24));
    }

    [Theory]
    [InlineData("SameCityDays", "0")]
    [InlineData("SameCityDays", "-2")]
    [InlineData("OtherCityDays", "abc")]
    [InlineData("OtherCityDays", "1.5")]
    [InlineData("HandoverHours", "0")]
    [InlineData("SameCityDays", "100000")]
    public void A_bad_value_stops_startup(string key, string value)
    {
        var act = () => Read((key, value));
        act.Should().Throw<InvalidOperationException>().WithMessage($"*Delivery:{key}*");
    }
}
