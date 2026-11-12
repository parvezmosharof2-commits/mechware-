using System.Text.Json;
using FluentAssertions;
using Mechware.Application.Notifications;
using Mechware.Infrastructure.Notifications;
using Microsoft.Extensions.Logging.Abstractions;

namespace Mechware.IntegrationTests.Notifications;

/// <summary>Skips when no Mailpit answers; set <c>MECHWARE_TEST_MAILPIT_API</c> (default http://localhost:8025) and <c>MECHWARE_TEST_MAILPIT_SMTP_PORT</c> (default 1025).</summary>
public sealed class MailpitFactAttribute : FactAttribute
{
    public MailpitFactAttribute()
    {
        if (!MailpitInfra.Available) Skip = "Mailpit not reachable (docker compose up -d mailpit).";
    }
}

internal static class MailpitInfra
{
    public static readonly string Api = (Environment.GetEnvironmentVariable("MECHWARE_TEST_MAILPIT_API") ?? "http://localhost:8025").TrimEnd('/');
    public static readonly int SmtpPort = int.TryParse(Environment.GetEnvironmentVariable("MECHWARE_TEST_MAILPIT_SMTP_PORT"), out var p) ? p : 1025;
    public static readonly bool Available = Probe();

    private static bool Probe()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            return http.GetAsync(Api + "/api/v1/info").GetAwaiter().GetResult().IsSuccessStatusCode;
        }
        catch { return false; }
    }
}

public class SmtpEmailSenderTests
{
    [MailpitFact]
    public async Task SendsMultipartMessage_WithSubjectBothPartsAndFrom()
    {
        var recipient = $"buyer-{Guid.NewGuid():N}@example.test";
        var templates = new EmbeddedTemplates(NullLogger<EmbeddedTemplates>.Instance);
        var sender = new SmtpEmailSender(
            new EmailOptions { Host = "localhost", Port = MailpitInfra.SmtpPort, UseTls = false, FromAddress = "no-reply@mechware.test", FromName = "Mechware" },
            NullLogger<SmtpEmailSender>.Instance);

        var rendered = templates.Render("order-confirmed", "bn", new Dictionary<string, string>
        {
            ["customerName"] = "রহিম <b>",
            ["orderNumber"] = "MW-9001",
            ["total"] = "BDT 9,500",
            ["orderUrl"] = "https://mechware.test/orders/9001",
        });
        await sender.SendEmailAsync(new EmailMessage(recipient, rendered.Subject, rendered.Html, rendered.Text));

        using var http = new HttpClient();
        string? id = null;
        for (int i = 0; i < 20 && id is null; i++)
        {
            using var list = JsonDocument.Parse(await http.GetStringAsync($"{MailpitInfra.Api}/api/v1/messages"));
            foreach (var m in list.RootElement.GetProperty("messages").EnumerateArray())
                if (m.GetProperty("To").EnumerateArray().Any(t => t.GetProperty("Address").GetString() == recipient))
                    id = m.GetProperty("ID").GetString();
            if (id is null) await Task.Delay(250);
        }
        id.Should().NotBeNull("Mailpit should have received the message");

        using var msg = JsonDocument.Parse(await http.GetStringAsync($"{MailpitInfra.Api}/api/v1/message/{id}"));
        var root = msg.RootElement;
        root.GetProperty("Subject").GetString().Should().Be("অর্ডার MW-9001 নিশ্চিত হয়েছে");
        root.GetProperty("From").GetProperty("Address").GetString().Should().Be("no-reply@mechware.test");
        root.GetProperty("From").GetProperty("Name").GetString().Should().Be("Mechware");
        root.GetProperty("Text").GetString().Should().Contain("প্রিয় রহিম <b>,").And.Contain("https://mechware.test/orders/9001");
        var html = root.GetProperty("HTML").GetString();
        html.Should().Contain("রহিম &lt;b&gt;").And.Contain("href=\"https://mechware.test/orders/9001\"").And.NotContain("<b>,");
    }
}
