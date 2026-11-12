using FluentAssertions;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;

namespace Mechware.UnitTests.Identity;

/// <summary>F3a.2a destination rule: only Bangladeshi mobiles receive OTP SMS, every spelling counts as one number.</summary>
public class BdMobileNumberTests
{
    [Theory]
    [InlineData("+8801711000001", "+8801711000001")]
    [InlineData("8801711000001", "+8801711000001")]
    [InlineData("01711000001", "+8801711000001")]
    [InlineData("008801711000001", "+8801711000001")]
    [InlineData(" +880 1711-000001 ", "+8801711000001")]
    [InlineData("(017) 11.000.001", "+8801711000001")]
    [InlineData("০১৭১১০০০০০১", "+8801711000001")]   // Bengali digits
    [InlineData("01311000000", "+8801311000000")]   // operator digits 3-9
    [InlineData("01999999999", "+8801999999999")]
    public void BangladeshiMobiles_InAnySpelling_BecomeOneCanonicalForm(string input, string expected)
    {
        BdMobileNumber.TryNormalize(input, out string normalized).Should().BeTrue();
        normalized.Should().Be(expected);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("+14155550123")]       // US
    [InlineData("+447700900123")]      // UK
    [InlineData("+8801211000001")]     // operator digit 2 does not exist
    [InlineData("+8801011000001")]
    [InlineData("+880211000001")]      // Dhaka landline, not a mobile
    [InlineData("+88017110000012")]    // too long
    [InlineData("+880171100000")]      // too short
    [InlineData("1711000001")]         // no trunk prefix: ambiguous, refused
    [InlineData("+01711000001")]       // "+" with a national number
    [InlineData("01711000001;+1415")]  // smuggled second number
    [InlineData("017110OOOO1")]        // letters
    [InlineData("+8801711000001x")]
    [InlineData("8801711000001+")]     // "+" anywhere but first
    [InlineData("user@example.com")]
    [InlineData("0000000000000000000000000000000001711000001")]   // over the length cap
    public void EverythingElse_IsRefused(string? input)
    {
        BdMobileNumber.TryNormalize(input, out string normalized).Should().BeFalse();
        normalized.Should().BeEmpty();
    }

    [Fact]
    public void DefaultLimits_AreTheAgreedValues_AndValid()
    {
        var options = new OtpSmsLimitsOptions();
        options.ResendIntervalSeconds.Should().Be(60);
        options.PerNumberShortLimit.Should().Be(3);
        options.PerNumberShortWindowSeconds.Should().Be(600);
        options.PerNumberDailyLimit.Should().Be(10);
        options.PerIpHourlyLimit.Should().Be(20);
        options.GlobalPerMinuteLimit.Should().Be(100);
        options.IsValid().Should().BeTrue();

        new OtpSmsLimitsOptions { GlobalPerMinuteLimit = 0 }.IsValid().Should().BeFalse();
        new OtpSmsLimitsOptions { KeyPrefix = "a}b" }.IsValid().Should().BeFalse();
    }
}
