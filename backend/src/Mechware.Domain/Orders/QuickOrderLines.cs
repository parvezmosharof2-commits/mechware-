using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Mechware.Domain.Catalog;

namespace Mechware.Domain.Orders;

/// <summary>One parsed quick-order / BOM line. Duplicates of one part key are merged: <see cref="LineNumbers"/> lists every source line.</summary>
/// <param name="Raw">The first source line, cleaned (control characters removed, NFC).</param>
/// <param name="PartRaw">The part number as typed (first source line).</param>
/// <param name="PartKey">The exact <see cref="PartNumbers.Normalise"/> key.</param>
/// <param name="FuzzyKey">The <see cref="PartNumbers.Fuzzy"/> key (O→0, I/L→1), for did-you-mean.</param>
public sealed record QuickOrderLine(IReadOnlyList<int> LineNumbers, string Raw, string PartRaw, string PartKey, string FuzzyKey, int Quantity);

/// <summary>An error or notice. <see cref="Code"/> is stable (the web translates it); <see cref="Message"/> is English.</summary>
public sealed record QuickOrderIssue(int LineNumber, string Code, string Message, IReadOnlyList<int>? RelatedLines = null);

/// <param name="InputError">Set when the whole input is refused (too many lines / too large); then <see cref="Lines"/> is empty.</param>
public sealed record QuickOrderParseResult(
    IReadOnlyList<QuickOrderLine> Lines,
    IReadOnlyList<QuickOrderIssue> Errors,
    IReadOnlyList<QuickOrderIssue> Notices,
    QuickOrderIssue? InputError,
    int ContentLines)
{
    public bool IsClean => InputError is null && Errors.Count == 0;
}

/// <summary>
/// Pure parser for pasted quick-order text, an Excel paste or CSV text (also the RFQ BOM). No I/O.
/// Rules, in this order, per content line (blank lines, <c>#</c> comments and one leading header line are skipped):
/// 1. A tab, else a <c>;</c>, else a comma separates exactly two fields <c>part, qty</c> (quotes honoured; extra empty trailing
///    fields ignored; more fields = TOO_MANY_FIELDS). An empty qty field = QTY_MISSING. Exception: a comma-only line whose end
///    is a thousands number ("ABC 1,000", "ABC,1,000") is read as part + qty 1000; one ending " 2,5" (space, digits, comma,
///    1-2 digits) is QTY_NOT_INTEGER (a decimal), never part "ABC 2" + qty 5 (write "ABC 2, 5" for that).
/// 2. <c>qty x part</c> ("10 x LC1D09", "10x LC1D09"; x, X, × or *, whitespace needed after it) and <c>part x qty</c> ("LC1D09 x 10").
/// 3. Whitespace: with 2+ tokens and a numeric-looking LAST token, that token is the qty and the rest is the part number
///    ("6ES7 214-1AG40-0XB0 5"). Known ambiguity: a part number whose own last word is a number ("DR 2 5") reads the 5 as qty;
///    the buyer fixes it with a comma. A numeric-looking last token that is not a positive integer is an error, never part of the part.
/// 4. Otherwise the whole line is the part number, qty 1, notice QTY_DEFAULTED.
/// Qty: Bangla digits and thousands separators accepted; decimals refused (pieces); 1..<see cref="MaxQuantity"/>.
/// Same <c>PartKey</c> on several lines is merged (qty summed, notice DUPLICATE_MERGED); look-alike keys (O/0) are NOT merged.
/// </summary>
public static partial class QuickOrderLines
{
    public const int MaxLines = 500;
    public const int MaxLineLength = 200;
    public const int MaxInputLength = 100_000;
    public const int MaxQuantity = 1_000_000;

    private static readonly HashSet<string> HeaderWords = new(StringComparer.Ordinal)
    {
        "part", "no", "number", "partno", "pn", "mpn", "sku", "item", "code", "qty", "quantity", "pcs", "order",
        "পার্ট", "পরিমাণ", "নম্বর", "নং", "কোড", "পণ্য", "সংখ্যা", "অর্ডার",
    };

    [GeneratedRegex(@"^(\d[\d,]*)\s*[xX×*]\s+(\S.*)$")] private static partial Regex QtyXPart();
    [GeneratedRegex(@"^(\S.*?)\s+[xX×*]\s+(\d[\d,]*)$")] private static partial Regex PartXQty();
    [GeneratedRegex(@"^(\S.*?)[ ,]+(\d{1,3}(?:,\d{3})+)$")] private static partial Regex ThousandsTail();
    [GeneratedRegex(@"^\S.*\s\d+,\d{1,2}$")] private static partial Regex DecimalCommaTail();
    [GeneratedRegex(@"^[+-]?\d+(?:[.,]\d+)*$")] private static partial Regex NumericLike();
    [GeneratedRegex(@"^\d{1,3}(?:,\d{3})+$")] private static partial Regex Thousands();

    public static QuickOrderParseResult Parse(string? text)
    {
        var lines = new List<QuickOrderLine>();
        var errors = new List<QuickOrderIssue>();
        var notices = new List<QuickOrderIssue>();
        if (string.IsNullOrWhiteSpace(text)) return new(lines, errors, notices, null, 0);
        if (text.Length > MaxInputLength)
            return Refused("INPUT_TOO_LARGE", $"The input is longer than {MaxInputLength:N0} characters.", 0);

        string cleaned;
        try { cleaned = text.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return Refused("INPUT_INVALID", "The input is not valid text.", 0); }

        string[] physical = cleaned.Split('\n');
        var byKey = new Dictionary<string, int>(StringComparer.Ordinal);   // key → index in lines
        var sources = new List<List<int>>();
        int content = 0;
        bool headerChecked = false;

        for (int i = 0; i < physical.Length; i++)
        {
            int number = i + 1;
            string line = Clean(physical[i]);
            if (line.Trim().Length == 0 || line[0] == '#') continue;
            if (!headerChecked)
            {
                headerChecked = true;
                if (IsHeader(line)) continue;
            }

            if (++content > MaxLines)
                return Refused("TOO_MANY_LINES", $"More than {MaxLines} lines; send at most {MaxLines} per order.", content);
            if (line.Length > MaxLineLength)
            {
                errors.Add(new(number, "LINE_TOO_LONG", $"Line is longer than {MaxLineLength} characters."));
                continue;
            }

            if (!TryParseLine(line, out string part, out int qty, out string? code, out string? message, out bool defaulted))
            {
                errors.Add(new(number, code!, message!));
                continue;
            }

            string key = PartNumbers.Normalise(part);
            if (key.Length == 0) { errors.Add(new(number, "PART_INVALID", "No letters or digits in the part number.")); continue; }
            if (CountKeyChars(part) > PartNumbers.MaxLength)
            {
                errors.Add(new(number, "PART_TOO_LONG", $"Part number is longer than {PartNumbers.MaxLength} characters."));
                continue;
            }

            if (defaulted) notices.Add(new(number, "QTY_DEFAULTED", "No quantity given; 1 is used."));
            if (byKey.TryGetValue(key, out int at))
            {
                long total = (long)lines[at].Quantity + qty;
                if (total > MaxQuantity)
                {
                    errors.Add(new(number, "QTY_TOO_LARGE", $"Total quantity of this part would pass {MaxQuantity:N0}."));
                    continue;
                }

                sources[at].Add(number);
                lines[at] = lines[at] with { Quantity = (int)total, LineNumbers = sources[at] };
            }
            else
            {
                byKey[key] = lines.Count;
                sources.Add([number]);
                lines.Add(new(sources[^1], line, part, key, PartNumbers.Fuzzy(key), qty));
            }
        }

        for (int i = 0; i < lines.Count; i++)
            if (sources[i].Count > 1)
                notices.Add(new(sources[i][0], "DUPLICATE_MERGED", $"Part {lines[i].PartKey} appears on several lines; quantities added.", sources[i]));
        notices.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));
        errors.Sort((a, b) => a.LineNumber.CompareTo(b.LineNumber));
        return new(lines, errors, notices, null, content);

        static QuickOrderParseResult Refused(string code, string message, int content) =>
            new([], [], [], new QuickOrderIssue(0, code, message), content);
    }

    private static string Clean(string raw)
    {
        var sb = new StringBuilder(raw.Length);
        foreach (char c in raw)
        {
            if (c == '\t') sb.Append(c);
            else if (c is ' ' or '　') sb.Append(' ');
            else if (char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.Format) continue;
            else sb.Append(c);
        }

        return sb.ToString().Trim(' ');
    }

    private static bool IsHeader(string line)
    {
        string[] words = line.Split([' ', '\t', ',', ';', '"'], StringSplitOptions.RemoveEmptyEntries);
        return words.Length > 0 && words.All(w => HeaderWords.Contains(w.Trim('.', '#', ':').ToLowerInvariant()));
    }

    private static int CountKeyChars(string s) => s.Count(c => c is >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '০' and <= '৯');

    private static string Ascii(string s)
    {
        if (s.All(c => c is not (>= '০' and <= '৯'))) return s;
        return string.Create(s.Length, s, static (span, src) =>
        {
            for (int i = 0; i < src.Length; i++) span[i] = src[i] is >= '০' and <= '৯' ? (char)('0' + (src[i] - '০')) : src[i];
        });
    }

    private static bool TryParseLine(string line, out string part, out int qty, out string? code, out string? message, out bool defaulted)
    {
        part = string.Empty; qty = 1; code = message = null; defaulted = false;
        string a = Ascii(line);   // 1:1 character mapping, so indexes into `a` are valid in `line`

        char sep = line.Contains('\t') ? '\t' : line.Contains(';') ? ';' : line.Contains(',') ? ',' : '\0';
        if (sep == ',' && !line.Contains('"') && ThousandsTail().Match(a) is { Success: true } tail)
        {
            part = line[..tail.Groups[1].Length].Trim(' ', ',');
            return ParseQty(tail.Groups[2].Value, out qty, out code, out message);
        }

        if (sep == ',' && !line.Contains('"') && DecimalCommaTail().IsMatch(a))   // "LC1D09 2,5": a decimal, not part "LC1D09 2" + qty 5
            return Fail("QTY_NOT_INTEGER", "The quantity must be a whole number of pieces.", out code, out message);

        if (sep != '\0')
        {
            List<string> f = SplitFields(line, sep);
            while (f.Count > 2 && f[^1].Length == 0) f.RemoveAt(f.Count - 1);
            if (f.Count > 2) return Fail("TOO_MANY_FIELDS", "Use two columns: part number and quantity.", out code, out message);
            if (f[0].Length == 0) return Fail("PART_MISSING", "The part number is empty.", out code, out message);
            part = f[0];
            if (f.Count == 1) { defaulted = true; return true; }
            return ParseQty(f[1], out qty, out code, out message);
        }

        if (QtyXPart().Match(a) is { Success: true } m1)
        {
            part = line[m1.Groups[2].Index..].Trim();
            return ParseQty(m1.Groups[1].Value, out qty, out code, out message);
        }

        if (PartXQty().Match(a) is { Success: true } m2)
        {
            part = line[..m2.Groups[1].Length].Trim();
            return ParseQty(m2.Groups[2].Value, out qty, out code, out message);
        }

        int cut = a.LastIndexOf(' ');
        if (cut > 0 && NumericLike().IsMatch(a[(cut + 1)..]))
        {
            part = line[..cut].Trim();
            return ParseQty(a[(cut + 1)..], out qty, out code, out message);
        }

        part = line;
        defaulted = true;
        return true;
    }

    private static bool Fail(string c, string m, out string? code, out string? message)
    {
        code = c; message = m;
        return false;
    }

    private static bool ParseQty(string field, out int qty, out string? code, out string? message)
    {
        qty = 0;
        string s = Ascii(field).Trim().Trim('"').Trim();
        if (s.Length == 0) return Fail("QTY_MISSING", "The quantity is empty.", out code, out message);
        if (Thousands().IsMatch(s)) s = s.Replace(",", "", StringComparison.Ordinal);
        if (s.All(char.IsAsciiDigit))
        {
            if (s.Length > 9 || !int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out qty))
                return Fail("QTY_TOO_LARGE", $"The quantity is more than {MaxQuantity:N0}.", out code, out message);
            if (qty < 1) return Fail("QTY_INVALID", "The quantity must be 1 or more.", out code, out message);
            if (qty > MaxQuantity) return Fail("QTY_TOO_LARGE", $"The quantity is more than {MaxQuantity:N0}.", out code, out message);
            code = message = null;
            return true;
        }

        if (NumericLike().IsMatch(s) && s[0] is not ('-' or '+'))
            return Fail("QTY_NOT_INTEGER", "The quantity must be a whole number of pieces.", out code, out message);
        return Fail("QTY_INVALID", "The quantity must be a positive whole number.", out code, out message);
    }

    private static List<string> SplitFields(string line, char sep)
    {
        var fields = new List<string>();
        var sb = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"')
            {
                if (quoted && i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; }
                else quoted = !quoted;
            }
            else if (c == sep && !quoted) { fields.Add(sb.ToString().Trim()); sb.Clear(); }
            else sb.Append(c);
        }

        fields.Add(sb.ToString().Trim());
        return fields;
    }
}
