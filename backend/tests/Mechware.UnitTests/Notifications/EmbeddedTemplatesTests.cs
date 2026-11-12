using FluentAssertions;
using Mechware.Application.Notifications;
using Mechware.Infrastructure.Notifications;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mechware.UnitTests.Notifications;

public class EmbeddedTemplatesTests
{
    private static readonly EmbeddedTemplates Engine = new(NullLogger<EmbeddedTemplates>.Instance);

    private static readonly Dictionary<string, Dictionary<string, string>> Models = new()
    {
        ["order-confirmed"] = new() { ["customerName"] = "Rahim", ["orderNumber"] = "MW-1001", ["total"] = "BDT 12,500", ["orderUrl"] = "https://mechware.test/orders/1001" },
        ["approval-needed"] = new() { ["approverName"] = "Karim", ["requesterName"] = "Rahim", ["orderNumber"] = "MW-1002", ["total"] = "BDT 80,000", ["approvalUrl"] = "https://mechware.test/approvals/1002" },
        ["quote-sent"] = new() { ["customerName"] = "Rahim", ["quoteNumber"] = "Q-77", ["total"] = "BDT 45,000", ["validUntil"] = "30 Nov 2026", ["quoteUrl"] = "https://mechware.test/quotes/77" },
    };

    private static readonly Dictionary<(string Key, string Locale), (string Subject, string[] TextContains)> Golden = new()
    {
        [("order-confirmed", "en")] = ("Order MW-1001 confirmed", ["Hello Rahim,", "order MW-1001. Order total: BDT 12,500.", "View your order: https://mechware.test/orders/1001"]),
        [("order-confirmed", "bn")] = ("অর্ডার MW-1001 নিশ্চিত হয়েছে", ["প্রিয় Rahim,", "মোট মূল্য: BDT 12,500।", "অর্ডার দেখুন: https://mechware.test/orders/1001"]),
        [("approval-needed", "en")] = ("Approval needed for order MW-1002", ["Hello Karim,", "Rahim has placed order MW-1002 (BDT 80,000)", "Review and approve: https://mechware.test/approvals/1002"]),
        [("approval-needed", "bn")] = ("অর্ডার MW-1002-এর জন্য আপনার অনুমোদন প্রয়োজন", ["প্রিয় Karim,", "Rahim অর্ডার MW-1002 (BDT 80,000) দিয়েছেন", "দেখে অনুমোদন করুন: https://mechware.test/approvals/1002"]),
        [("quote-sent", "en")] = ("Your quote Q-77 from Mechware", ["Hello Rahim,", "Total: BDT 45,000. It is valid until 30 Nov 2026.", "View your quote: https://mechware.test/quotes/77"]),
        [("quote-sent", "bn")] = ("Mechware থেকে আপনার কোটেশন Q-77", ["প্রিয় Rahim,", "মোট: BDT 45,000। কোটেশনটি 30 Nov 2026 পর্যন্ত বৈধ।", "কোটেশন দেখুন: https://mechware.test/quotes/77"]),
    };

    public static IEnumerable<object[]> Cases() => Golden.Keys.Select(k => new object[] { k.Key, k.Locale });

    [Theory]
    [MemberData(nameof(Cases))]
    public void EveryTemplateAndLocale_RendersTheGoldenSubjectAndText(string key, string locale)
    {
        var r = Engine.Render(key, locale, Models[key]);
        var (subject, textContains) = Golden[(key, locale)];

        r.Subject.Should().Be(subject);
        foreach (var part in textContains) r.Text.Should().Contain(part);
        r.Text.Should().StartWith("MECHWARE\n").And.Contain("--\n").And.NotContain("{{");
        r.Html.Should().Contain($"<html lang=\"{locale}\">").And.Contain("MECHWARE").And.NotContain("{{")
            .And.Contain("href=\"https://mechware.test/").And.Contain(Models[key].Values.First());
    }

    [Fact]
    public void Footer_SaysWhyYouGetTheEmail_InBothLocales()
    {
        var en = Engine.Render("order-confirmed", "en", Models["order-confirmed"]);
        var bn = Engine.Render("order-confirmed", "bn", Models["order-confirmed"]);
        en.Html.Should().Contain("You get this email because"); en.Text.Should().Contain("You get this email because");
        bn.Html.Should().Contain("আপনি এই ইমেইলটি পাচ্ছেন কারণ"); bn.Text.Should().Contain("আপনি এই ইমেইলটি পাচ্ছেন কারণ");
    }

    [Fact]
    public void Html_EncodesValues_ButTextStaysPlain()
    {
        var model = new Dictionary<string, string>(Models["order-confirmed"]) { ["customerName"] = "<script>alert(1)</script> & Co" };
        var r = Engine.Render("order-confirmed", "en", model);

        r.Html.Should().Contain("&lt;script&gt;alert(1)&lt;/script&gt; &amp; Co").And.NotContain("<script>");
        r.Text.Should().Contain("Hello <script>alert(1)</script> & Co,");
    }

    [Fact]
    public void Html_UsesInlineCssOnly_NoExternalAssets()
    {
        var html = Engine.Render("quote-sent", "en", Models["quote-sent"]).Html;
        html.Should().NotContain("<link").And.NotContain("<script").And.NotContain("<img").And.NotContain("@import").And.NotContain("src=");
    }

    [Fact]
    public void Subject_NeverContainsLineBreaks()
    {
        var model = new Dictionary<string, string>(Models["order-confirmed"]) { ["orderNumber"] = "A\r\nBcc: x@evil.test" };
        Engine.Render("order-confirmed", "en", model).Subject.Should().NotContain("\n").And.NotContain("\r");
    }

    [Fact]
    public void MissingPlaceholder_Throws()
    {
        var model = new Dictionary<string, string>(Models["order-confirmed"]);
        model.Remove("total");
        var act = () => Engine.Render("order-confirmed", "en", model);
        act.Should().Throw<NotificationTemplateException>().WithMessage("*'total'*");
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("http://evil.example/orders/1")]
    [InlineData("//evil.example/orders/1")]
    [InlineData("/orders/1")]
    [InlineData("https://user:pass@evil.example/")]
    public void UnsafeLink_IsRefused(string url)
    {
        var model = new Dictionary<string, string>(Models["order-confirmed"]) { ["orderUrl"] = url };
        var act = () => Engine.Render("order-confirmed", "en", model);
        act.Should().Throw<NotificationTemplateException>().WithMessage("*'orderUrl'*https*");
    }

    [Theory]
    [InlineData("https://mechware.com.bd/orders/1")]
    [InlineData("http://localhost:3000/orders/1")]
    public void SafeLink_IsAccepted(string url)
    {
        var model = new Dictionary<string, string>(Models["order-confirmed"]) { ["orderUrl"] = url };
        Engine.Render("order-confirmed", "en", model).Html.Should().Contain(url);
    }

    [Fact]
    public void UnknownKey_Throws()
    {
        var act = () => Engine.Render("no-such-template", "en", new Dictionary<string, string>());
        act.Should().Throw<NotificationTemplateException>().WithMessage("*Unknown template*");
    }

    [Theory]
    [InlineData("../secrets")]
    [InlineData("Order Confirmed")]
    [InlineData("")]
    public void MalformedKey_Throws(string key)
    {
        var act = () => Engine.Render(key, "en", new Dictionary<string, string>());
        act.Should().Throw<NotificationTemplateException>();
    }

    [Fact]
    public void UnsupportedLocale_Throws()
    {
        var act = () => Engine.Render("order-confirmed", "fr", Models["order-confirmed"]);
        act.Should().Throw<NotificationTemplateException>().WithMessage("*locale*");
    }

    [Fact]
    public void MissingBnFile_FallsBackToEn_AndWarns()
    {
        var files = new Dictionary<string, string>
        {
            ["only-en.en.subject.txt"] = "Hello {{name}}",
            ["only-en.en.html"] = "<p>{{name}}</p>",
            ["only-en.en.txt"] = "{{name}}",
            ["_layout.en.html"] = "<html>{{content}}</html>",
            ["_layout.en.txt"] = "{{content}}",
        };
        var log = new CapturingLogger();
        var engine = new EmbeddedTemplates(log, n => files.GetValueOrDefault(n));

        var r = engine.Render("only-en", "bn", new Dictionary<string, string> { ["name"] = "Rahim" });

        r.Subject.Should().Be("Hello Rahim");
        log.Entries.Should().ContainSingle(e => e.Level == LogLevel.Warning && e.Message.Contains("only-en"));
    }

    [Fact]
    public void BnFilePresent_DoesNotWarn()
    {
        var log = new CapturingLogger();
        new EmbeddedTemplates(log).Render("order-confirmed", "bn", Models["order-confirmed"]);
        log.Entries.Should().BeEmpty();
    }

    private sealed class CapturingLogger : ILogger<EmbeddedTemplates>
    {
        public List<(LogLevel Level, string Message)> Entries { get; } = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Entries.Add((logLevel, formatter(state, exception)));
    }
}
