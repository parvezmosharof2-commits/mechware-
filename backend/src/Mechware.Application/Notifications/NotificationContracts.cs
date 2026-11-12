namespace Mechware.Application.Notifications;

/// <summary>A notification to render from a template and send. <see cref="Locale"/> is <c>en</c> or <c>bn</c>.</summary>
public sealed record NotificationMessage(string To, string Locale, string TemplateKey, IReadOnlyDictionary<string, string> Model);

/// <summary>A finished email: subject plus both bodies (multipart/alternative).</summary>
public sealed record EmailMessage(string To, string Subject, string HtmlBody, string TextBody);

/// <summary>The rendered parts of one template.</summary>
public sealed record RenderedEmail(string Subject, string Html, string Text);

/// <summary>Sends email. The only implementation that talks to a mail server is in Infrastructure.</summary>
public interface INotificationSender
{
    Task SendEmailAsync(EmailMessage message, CancellationToken cancellationToken = default);
}

/// <summary>
/// Renders a template key in a locale. Placeholders are <c>{{name}}</c>; values are HTML-encoded in the html part.
/// A missing placeholder value or an unknown key throws <see cref="NotificationTemplateException"/>.
/// </summary>
public interface INotificationTemplates
{
    RenderedEmail Render(string templateKey, string locale, IReadOnlyDictionary<string, string> model);
}

public sealed class NotificationTemplateException(string message) : Exception(message);
