using System.Text;

namespace ShopVerse.Domain.Catalog;

/// <summary>
/// Manufacturer part numbers as buyers type them (brief: exact part-number hits ≥ 98% "any formatting"). The key keeps only
/// A–Z and 0–9, upper case, so "6ES7 214-1AG40-0XB0", "6es7214-1ag40-0xb0" and "6ES7.214.1AG40.0XB0" are one key. Bangla
/// digits (০–৯) count as digits: people type part numbers on Bangla keyboards. The fuzzy key also reads O as 0 and I or L as
/// 1, for a second look when the exact key finds nothing (on a phone those are easy to mix up).
/// </summary>
public static class PartNumbers
{
    public const int MaxLength = 64;

    /// <summary>The comparison key; empty when nothing usable is left. Longer input is cut at <see cref="MaxLength"/>.</summary>
    public static string Normalise(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var key = new StringBuilder(Math.Min(raw.Length, MaxLength));
        foreach (char c in raw)
        {
            if (key.Length == MaxLength) break;
            if (c is >= '0' and <= '9' or >= 'A' and <= 'Z') key.Append(c);
            else if (c is >= 'a' and <= 'z') key.Append((char)(c - 'a' + 'A'));
            else if (c is >= '০' and <= '৯') key.Append((char)('0' + (c - '০')));   // Bangla digits
        }

        return key.ToString();
    }

    /// <summary>The key with the look-alike letters read as digits (O→0, I→1, L→1).</summary>
    public static string Fuzzy(string key) => key.Replace('O', '0').Replace('I', '1').Replace('L', '1');

    /// <summary>
    /// Whether a search text is worth an exact part-number look-up first: at most four words, a key of at least four
    /// characters, and a word that mixes letters and digits ("6ES7", "LC1-D09") or is four or more digits ("2900011").
    /// "inverter 2 hp" or "siemens" are searched as words. A false yes only costs one indexed look-up before the search.
    /// </summary>
    public static bool LooksLikePartNumber(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        string trimmed = text.Trim();
        string[] words = trimmed.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (trimmed.Length > MaxLength * 2 || words.Length > 4 || Normalise(trimmed).Length < 4) return false;
        return words.Any(w =>
        {
            string key = Normalise(w);
            bool digit = key.Any(char.IsAsciiDigit), letter = key.Any(char.IsAsciiLetter);
            return (digit && letter) || (digit && key.Length >= 4);
        });
    }
}
