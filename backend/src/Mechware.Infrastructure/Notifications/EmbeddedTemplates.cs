using System.Net;
using System.Text.RegularExpressions;
using Mechware.Application.Notifications;
using Microsoft.Extensions.Logging;

namespace Mechware.Infrastructure.Notifications;

/// <summary>
/// Templates are embedded files <c>{key}.{locale}.subject.txt</c>, <c>.html</c>, <c>.txt</c> plus <c>_layout.{locale}.html|txt</c>
/// (the layout holds <c>{{content}}</c>). Bangla falls back to English only when the bn file does not exist, with a warning.
/// </summary>
public sealed partial class EmbeddedTemplates : INotificationTemplates
{
    private const string Prefix = "Mechware.Notifications.";
    private static readonly string[] Locales = ["en", "bn"];

    private readonly ILogger<EmbeddedTemplates> _logger;
    private readonly Func<string, string?> _read;

    public EmbeddedTemplates(ILogger<EmbeddedTemplates> logger, Func<string, string?>? reader = null)
    {
        _logger = logger;
        _read = reader ?? ReadEmbedded;
    }

    public RenderedEmail Render(string templateKey, string locale, IReadOnlyDictionary<string, string> model)
    {
        if (!Locales.Contains(locale)) throw new NotificationTemplateException($"Unsupported locale '{locale}'.");
        if (string.IsNullOrWhiteSpace(templateKey) || !KeyPattern().IsMatch(templateKey))
            throw new NotificationTemplateException("Invalid template key.");

        string used = locale;
        if (_read($"{templateKey}.{locale}.subject.txt") is null)
        {
            if (locale == "bn" && _read($"{templateKey}.en.subject.txt") is not null)
            {
                _logger.LogWarning("Template {TemplateKey} has no bn version; falling back to en", templateKey);
                used = "en";
            }
            else throw new NotificationTemplateException($"Unknown template '{templateKey}'.");
        }

        string subject = Fill(Load($"{templateKey}.{used}.subject.txt"), model, encode: false, templateKey).ReplaceLineEndings(" ").Trim();
        string htmlBody = Fill(Load($"{templateKey}.{used}.html"), model, encode: true, templateKey);
        string textBody = Fill(Load($"{templateKey}.{used}.txt"), model, encode: false, templateKey);

        string html = Load($"_layout.{used}.html").Replace("{{title}}", WebUtility.HtmlEncode(subject), StringComparison.Ordinal)
            .Replace("{{content}}", htmlBody, StringComparison.Ordinal);
        string text = Load($"_layout.{used}.txt").Replace("{{content}}", textBody, StringComparison.Ordinal);
        return new RenderedEmail(subject, html, text);
    }

    private string Load(string name) => _read(name) ?? throw new NotificationTemplateException($"Template file '{name}' is missing.");

    private static string Fill(string template, IReadOnlyDictionary<string, string> model, bool encode, string key) =>
        PlaceholderPattern().Replace(template, m =>
        {
            string name = m.Groups[1].Value;
            if (!model.TryGetValue(name, out var value) || value is null)
                throw new NotificationTemplateException($"Template '{key}' needs a value for '{name}'.");
            if (name.EndsWith("Url", StringComparison.Ordinal) && !IsSafeLink(value))
                throw new NotificationTemplateException($"Template '{key}': '{name}' must be an absolute https URL.");
            return encode ? WebUtility.HtmlEncode(value) : value;
        });

    /// <summary>
    /// Values named <c>*Url</c> go into <c>href</c>, where HTML-encoding does not stop <c>javascript:</c> or <c>data:</c>.
    /// Only absolute https links pass (http only for localhost, for development mail).
    /// </summary>
    internal static bool IsSafeLink(string value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri)
        && (uri.Scheme == Uri.UriSchemeHttps || (uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback))
        && string.IsNullOrEmpty(uri.UserInfo);

    private static string? ReadEmbedded(string name)
    {
        using var stream = typeof(EmbeddedTemplates).Assembly.GetManifestResourceStream(Prefix + name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n");
    }

    [GeneratedRegex(@"\{\{\s*([A-Za-z][A-Za-z0-9_]*)\s*\}\}")]
    private static partial Regex PlaceholderPattern();

    [GeneratedRegex("^[a-z][a-z0-9-]{0,63}$")]
    private static partial Regex KeyPattern();
}
