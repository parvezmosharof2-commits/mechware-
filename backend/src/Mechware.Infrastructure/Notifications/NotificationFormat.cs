using System.Globalization;

namespace Mechware.Infrastructure.Notifications;

/// <summary>Where the storefront lives, for links in emails. Always https, or http on localhost in Development/Testing.</summary>
public sealed record StorefrontLinks(string BaseUrl)
{
    /// <summary>Absolute link to <paramref name="path"/> (segments already escaped), under the locale prefix.</summary>
    public string Url(string locale, string path) => $"{BaseUrl.TrimEnd('/')}/{NotificationFormat.Locale(locale)}/{path.TrimStart('/')}";
}

/// <summary>Pure helpers for building email model values.</summary>
public static class NotificationFormat
{
    public const string DevelopmentBaseUrl = "http://localhost:3000";

    /// <summary>"BDT 12,500.00" (invariant digits and separators, so the text is the same in both languages).</summary>
    public static string Money(long minorUnits, string currency = "BDT")
    {
        var sign = minorUnits < 0 ? "-" : "";
        var abs = Math.Abs((decimal)minorUnits) / 100m;
        return $"{currency} {sign}{abs.ToString("N2", CultureInfo.InvariantCulture)}";
    }

    /// <summary><c>bn</c> or <c>en</c>; anything else (or none) is <c>en</c>.</summary>
    public static string Locale(string? locale) =>
        string.Equals(locale?.Trim(), "bn", StringComparison.OrdinalIgnoreCase) ? "bn" : "en";

    /// <summary>Validated base URL without a trailing slash; throws when it is not allowed.</summary>
    public static string NormalizeBaseUrl(string? configured, bool requireRealSetup)
    {
        if (string.IsNullOrWhiteSpace(configured))
        {
            if (requireRealSetup) throw new InvalidOperationException("Storefront:BaseUrl is not set; set Storefront__BaseUrl to the public https address of the shop.");
            return DevelopmentBaseUrl;
        }

        if (!Uri.TryCreate(configured.Trim(), UriKind.Absolute, out var uri) || !string.IsNullOrEmpty(uri.Query) || !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException("Storefront:BaseUrl must be an absolute URL without a query or fragment.");
        bool localHttp = uri.Scheme == Uri.UriSchemeHttp && uri.IsLoopback && !requireRealSetup;
        if (uri.Scheme != Uri.UriSchemeHttps && !localHttp)
            throw new InvalidOperationException("Storefront:BaseUrl must be https (http only for localhost in Development).");
        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }
}
