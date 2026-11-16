using Ganss.Xss;

namespace Mechware.Domain.Catalog;

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

    /// <summary>
    /// What the sanitizer would strip from <paramref name="html"/> (unknown tags, scripts, event handlers, disallowed link schemes,
    /// comments), one short text per problem; empty when the markup is entirely inside the allow-list. Used to refuse an edit
    /// outright instead of silently saving something other than what the editor sent.
    /// </summary>
    public static IReadOnlyList<string> FindProblems(string? html)
    {
        if (string.IsNullOrWhiteSpace(html)) return [];
        var problems = new List<string>();
        var checker = Build();   // own instance: the shared one must not carry per-call handlers
        checker.RemovingTag += (_, e) => problems.Add($"The tag <{e.Tag.LocalName}> is not allowed.");
        checker.RemovingAttribute += (_, e) => problems.Add($"The attribute '{e.Attribute.Name}' is not allowed.");
        checker.RemovingStyle += (_, e) => problems.Add($"The style '{e.Style.Name}' is not allowed.");
        checker.RemovingAtRule += (_, _) => problems.Add("CSS at-rules are not allowed.");
        checker.RemovingComment += (_, _) => problems.Add("HTML comments are not allowed.");
        checker.RemovingCssClass += (_, e) => problems.Add($"The CSS class '{e.CssClass}' is not allowed.");
        checker.Sanitize(html);
        return [.. problems.Distinct().Take(10)];
    }

    public static string Sanitize(string? html) => string.IsNullOrWhiteSpace(html) ? string.Empty : Sanitizer.Sanitize(html);
}
