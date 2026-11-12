using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using Mechware.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace Mechware.Infrastructure.Notifications;

/// <summary>SMTP adapter (System.Net.Mail). Sends multipart/alternative: text first, html second.</summary>
public sealed class SmtpEmailSender(EmailOptions options, ILogger<SmtpEmailSender> logger) : INotificationSender
{
    public async Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        using var mail = new MailMessage
        {
            From = new MailAddress(options.FromAddress, options.FromName, Encoding.UTF8),
            Subject = message.Subject.ReplaceLineEndings(" "), // no header injection through a value
            SubjectEncoding = Encoding.UTF8,
            BodyEncoding = Encoding.UTF8,
        };
        mail.To.Add(new MailAddress(message.To));
        mail.AlternateViews.Add(View(message.TextBody, MediaTypeNames.Text.Plain));
        mail.AlternateViews.Add(View(message.HtmlBody, MediaTypeNames.Text.Html));

        using var client = new SmtpClient(options.Host, options.Port)
        {
            EnableSsl = options.UseTls,
            Timeout = options.TimeoutSeconds * 1000,
            DeliveryMethod = SmtpDeliveryMethod.Network,
        };
        if (!string.IsNullOrEmpty(options.Username))
            client.Credentials = new NetworkCredential(options.Username, options.Password);

        await client.SendMailAsync(mail, cancellationToken);
        logger.LogInformation("Email sent to {Recipient}", Mask(message.To));
    }

    private static AlternateView View(string body, string mediaType) =>
        new(new MemoryStream(Encoding.UTF8.GetBytes(body)), new ContentType(mediaType) { CharSet = "utf-8" })
        { TransferEncoding = TransferEncoding.Base64 };

    internal static string Mask(string address)
    {
        int at = address.IndexOf('@');
        return at <= 1 ? "***" : address[0] + "***" + address[at..];
    }
}

/// <summary>Development/Testing fallback when no SMTP host is configured: logs a masked line, sends nothing.</summary>
public sealed class LoggingEmailSender(ILogger<LoggingEmailSender> logger) : INotificationSender
{
    public Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        logger.LogInformation("[MECHWARE-EMAIL-LOG] Would send \"{Subject}\" to {Recipient}", message.Subject, SmtpEmailSender.Mask(message.To));
        return Task.CompletedTask;
    }
}
