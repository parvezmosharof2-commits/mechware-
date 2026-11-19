using System.Globalization;
using System.Text;

namespace Mechware.Domain.Quotes;

/// <summary>
/// Free text of an RFQ or a quote as it is stored (ADR-0011 §14, QT17): plain text, never markup. Control characters and invisible
/// format characters (zero-width space, byte-order mark, the bidirectional overrides that make a file name or a price read
/// backwards) are removed; a new line and a tab stay, a carriage return goes. The zero-width joiner and non-joiner stay too, because
/// Bangla spelling needs them. A broken surrogate becomes U+FFFD, and the result is NFC, as the quick-order parser does.
/// </summary>
public static class PlainText
{
    public static string? Clean(string? value)
    {
        if (value is null) return null;
        var sb = new StringBuilder(value.Length);
        foreach (var rune in value.EnumerateRunes())
        {
            if (rune.Value is '\n' or '\t' or 0x200C or 0x200D) sb.Append(rune.ToString());
            else if (Rune.IsControl(rune) || Rune.GetUnicodeCategory(rune) == UnicodeCategory.Format) continue;
            else sb.Append(rune.ToString());
        }

        return sb.ToString().Normalize(NormalizationForm.FormC);
    }
}
