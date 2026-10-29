using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Domain.Common;

/// <summary>
/// Accepts only <c>https://</c> absolute URLs or root-relative paths. Blocks <c>javascript:</c>, <c>data:</c>,
/// protocol-relative (<c>//host</c>) and plain-http URLs that would end up in <c>href</c>/<c>src</c> attributes.
/// </summary>
public static class SafeUrl
{
    public const int MaxLength = 2048;

    public static bool IsSafe(string? url)
    {
        if (string.IsNullOrWhiteSpace(url) || url.Length > MaxLength) return false;
        if (url.Any(c => char.IsControl(c) || c == '\\' || char.IsWhiteSpace(c))) return false;
        if (url.StartsWith('/')) return !url.StartsWith("//", StringComparison.Ordinal);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == Uri.UriSchemeHttps && !string.IsNullOrEmpty(uri.Host);
    }

    public static string Require(string? url, string field)
    {
        if (!IsSafe(url)) throw new ValidationException(field, "URL must be https:// or a site-relative path.");
        return url!;
    }

    public static string? RequireOptional(string? url, string field) => url is null ? null : Require(url, field);

    /// <summary>Blur placeholders may be tiny inline images; nothing else may use the data: scheme.</summary>
    public static string? RequireBlurPlaceholder(string? value, string field)
    {
        if (value is null) return null;
        var ok = value.Length <= 4096 && (value.StartsWith("data:image/svg+xml;base64,", StringComparison.Ordinal)
            || value.StartsWith("data:image/png;base64,", StringComparison.Ordinal)
            || value.StartsWith("data:image/jpeg;base64,", StringComparison.Ordinal)
            || value.StartsWith("data:image/webp;base64,", StringComparison.Ordinal)) && value.All(c => !char.IsWhiteSpace(c));
        return ok ? value : throw new ValidationException(field, "Invalid blur placeholder.");
    }
}
