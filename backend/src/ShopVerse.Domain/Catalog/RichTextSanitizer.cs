using Ganss.Xss;

namespace ShopVerse.Domain.Catalog;

/// <summary>Allow-list HTML sanitizer for seller-authored descriptions (stored-XSS defence on write).</summary>
public static class RichTextSanitizer
{
    private static readonly HtmlSanitizer Sanitizer = Build();

    private static HtmlSanitizer Build()
    {
        var s = new HtmlSanitizer();
        s.AllowedTags.Clear();
        foreach (var tag in new[] { "p", "br", "h2", "h3", "h4", "ul", "ol", "li", "strong", "b", "em", "i", "u", "table", "thead", "tbody", "tr", "th", "td", "a", "span", "sup", "sub", "code" })
            s.AllowedTags.Add(tag);
        s.AllowedAttributes.Clear();
        foreach (var attr in new[] { "href", "title", "colspan", "rowspan" }) s.AllowedAttributes.Add(attr);
        s.AllowedSchemes.Clear();
        foreach (var scheme in new[] { "https", "mailto", "tel" }) s.AllowedSchemes.Add(scheme);
        s.AllowDataAttributes = false;
        // Relative URLs pass the scheme allow-list, so block the forms browsers resolve off-site:
        // protocol-relative (//host) and backslash variants (\\host, /\host).
        s.FilterUrl += (_, e) =>
        {
            var url = e.SanitizedUrl?.Trim();
            if (url is null) return;
            if (url.StartsWith("//", StringComparison.Ordinal) || url.Contains('\\')) e.SanitizedUrl = null;
        };
        return s;
    }

    public static string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? string.Empty : Sanitizer.Sanitize(html);
}
