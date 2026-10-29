using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Domain.Catalog;

/// <summary>URL-safe slug helper (lowercase ASCII, digits and single hyphens).</summary>
public static partial class Slug
{
    public const int MaxLength = 255;

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex NonAlnum();

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$")]
    private static partial Regex Valid();

    public static string From(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            throw new ValidationException("slug", "Slug source text is required.");
        }

        var normalized = text.Trim().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var ch in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        var slug = NonAlnum().Replace(sb.ToString().ToLowerInvariant(), "-").Trim('-');
        if (slug.Length == 0)
        {
            throw new ValidationException("slug", "Slug source text has no URL-safe characters.");
        }

        return slug.Length > MaxLength ? slug[..MaxLength].TrimEnd('-') : slug;
    }

    public static bool IsValid(string? slug) =>
        !string.IsNullOrEmpty(slug) && slug.Length <= MaxLength && Valid().IsMatch(slug);

    public static string Require(string slug)
    {
        if (!IsValid(slug))
        {
            throw new ValidationException("slug", $"'{slug}' is not a valid slug.");
        }

        return slug;
    }
}
