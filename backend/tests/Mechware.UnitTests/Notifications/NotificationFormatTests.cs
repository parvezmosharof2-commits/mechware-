using System.Net.Mail;
using FluentAssertions;
using Mechware.Application.Notifications;
using Mechware.Infrastructure.Notifications;

namespace Mechware.UnitTests.Notifications;

public class NotificationFormatTests
{
    [Theory]
    [InlineData(1_250_000, "BDT 12,500.00")]
    [InlineData(105, "BDT 1.05")]
    [InlineData(0, "BDT 0.00")]
    [InlineData(123_456_789, "BDT 1,234,567.89")]
    public void Money_IsFormattedWithGroupingAndTwoDecimals(long minor, string expected) =>
        NotificationFormat.Money(minor).Should().Be(expected);

    [Theory]
    [InlineData("bn", "bn")]
    [InlineData(" BN ", "bn")]
    [InlineData("en", "en")]
    [InlineData("fr", "en")]
    [InlineData(null, "en")]
    [InlineData("", "en")]
    public void Locale_IsBnOrElseEn(string? input, string expected) => NotificationFormat.Locale(input).Should().Be(expected);

    [Fact]
    public void BaseUrl_DefaultsToLocalhostInDevelopment_AndIsRequiredInProduction()
    {
        NotificationFormat.NormalizeBaseUrl(null, requireRealSetup: false).Should().Be("http://localhost:3000");
        FluentActions.Invoking(() => NotificationFormat.NormalizeBaseUrl(null, requireRealSetup: true)).Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("http://shop.example.com", false)]   // plain http to a real host
    [InlineData("http://localhost:3000", true)]      // localhost http is development only
    [InlineData("shop.example.com", true)]
    [InlineData("https://shop.example.com/?a=1", true)]
    public void BaseUrl_RefusesAnythingButHttps_ExceptLocalhostInDevelopment(string url, bool production) =>
        FluentActions.Invoking(() => NotificationFormat.NormalizeBaseUrl(url, production)).Should().Throw<InvalidOperationException>();

    [Fact]
    public void BaseUrl_AcceptsHttpsAndLocalhost_AndLinksCarryTheLocale()
    {
        var prod = new StorefrontLinks(NotificationFormat.NormalizeBaseUrl("https://shop.example.com/", true));
        prod.Url("bn", "account/orders/MW-1").Should().Be("https://shop.example.com/bn/account/orders/MW-1");
        new StorefrontLinks(NotificationFormat.NormalizeBaseUrl("http://localhost:3000", false)).Url("xx", "/a").Should().Be("http://localhost:3000/en/a");
    }

    [Fact]
    public void PermanentFailures_AreTemplateErrors_BadAddresses_AndMailboxRefusals_NotNetworkTrouble()
    {
        NotificationDispatcher.IsPermanent(new NotificationTemplateException("x")).Should().BeTrue();
        NotificationDispatcher.IsPermanent(new FormatException()).Should().BeTrue();
        NotificationDispatcher.IsPermanent(new SmtpException(SmtpStatusCode.MailboxUnavailable)).Should().BeTrue();
        NotificationDispatcher.IsPermanent(new SmtpException(SmtpStatusCode.MailboxBusy)).Should().BeFalse();
        NotificationDispatcher.IsPermanent(new SmtpException(SmtpStatusCode.ServiceNotAvailable)).Should().BeFalse();
        NotificationDispatcher.IsPermanent(new IOException()).Should().BeFalse();
        NotificationDispatcher.IsPermanent(new TimeoutException()).Should().BeFalse();
    }
}
