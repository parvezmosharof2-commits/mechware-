using System.Globalization;
using System.Text;

namespace Mechware.Domain.Credit;

/// <summary>Open receivables of one company (or of all) split by days past due (ADR-0010 §9): whole poisha, no rounding.</summary>
public readonly record struct CreditAgingBuckets(long Current, long Days1To30, long Days31To60, long Days61To90, long Days90Plus)
{
    public long Overdue => Days1To30 + Days31To60 + Days61To90 + Days90Plus;

    public long Total => Current + Overdue;

    public CreditAgingBuckets Plus(CreditAgingBuckets other) => new(Current + other.Current, Days1To30 + other.Days1To30,
        Days31To60 + other.Days31To60, Days61To90 + other.Days61To90, Days90Plus + other.Days90Plus);
}

/// <summary>
/// The aging rule: an order is overdue when today (Dhaka) is after its charge's <c>due_on</c> and its receivable is above zero, and
/// <c>days past due = today − due_on</c> (the same rule as the hold). <c>current</c> = not yet overdue (0 or fewer days), then
/// 1-30, 31-60, 61-90 and 91 or more ("90+"). A bucket holds the order's whole remaining receivable.
/// </summary>
public static class CreditAging
{
    public static int DaysPastDue(DateOnly dueOn, DateOnly today) => today.DayNumber - dueOn.DayNumber;

    public static CreditAgingBuckets Of(DateOnly dueOn, long receivableMinor, DateOnly today) => DaysPastDue(dueOn, today) switch
    {
        <= 0 => new(receivableMinor, 0, 0, 0, 0),
        <= 30 => new(0, receivableMinor, 0, 0, 0),
        <= 60 => new(0, 0, receivableMinor, 0, 0),
        <= 90 => new(0, 0, 0, receivableMinor, 0),
        _ => new(0, 0, 0, 0, receivableMinor)
    };
}

/// <summary>
/// CSV for finance exports (ADR-0010 §10, threat model CS27): UTF-8 with a byte-order mark, every cell quoted, a text cell that starts
/// with <c>= + - @</c>, a tab or a carriage return gets a leading <c>'</c> so a spreadsheet never runs it as a formula. Amounts are
/// numbers we format ourselves (poisha, and BDT with two decimals), so they are quoted but never prefixed: a negative stays a number.
/// </summary>
public static class CreditCsv
{
    public const string ByteOrderMark = "﻿";

    public static string Text(string? value)
    {
        var text = value ?? string.Empty;
        if (text.Length > 0 && text[0] is '=' or '+' or '-' or '@' or '\t' or '\r') text = "'" + text;
        return Quote(text);
    }

    public static string Minor(long poisha) => Quote(poisha.ToString(CultureInfo.InvariantCulture));

    /// <summary>Whole BDT with exactly two decimals, no grouping: 123456 → 1234.56, -5 → -0.05.</summary>
    public static string Bdt(long poisha)
    {
        var abs = poisha == long.MinValue ? (decimal)long.MaxValue + 1 : Math.Abs(poisha);
        return Quote(string.Create(CultureInfo.InvariantCulture, $"{(poisha < 0 ? "-" : string.Empty)}{(long)(abs / 100)}.{(long)(abs % 100):00}"));
    }

    public static string Row(params string[] cells) => string.Join(',', cells) + "\r\n";

    public static string Document(IEnumerable<string> rows)
    {
        var sb = new StringBuilder(ByteOrderMark);
        foreach (var row in rows) sb.Append(row);
        return sb.ToString();
    }

    private static string Quote(string text) => "\"" + text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
