using System.Globalization;
using System.Text;

namespace Mechware.Infrastructure.Quotes;

/// <summary>Numbers, money, dates and amounts in words for the quote PDF, in English or Bangla (Bangla digits and lakh/crore grouping, as the site shows them).</summary>
internal static class QuotePdfText
{
    private const string BnDigits = "০১২৩৪৫৬৭৮৯";

    private static readonly string[] BnMonths = ["জানুয়ারি", "ফেব্রুয়ারি", "মার্চ", "এপ্রিল", "মে", "জুন", "জুলাই", "আগস্ট", "সেপ্টেম্বর", "অক্টোবর", "নভেম্বর", "ডিসেম্বর"];

    // 0 to 99: Bangla number words are irregular, so each has its own word.
    private static readonly string[] Bn99 = ("শূন্য এক দুই তিন চার পাঁচ ছয় সাত আট নয় দশ এগারো বারো তেরো চৌদ্দ পনেরো ষোলো সতেরো আঠারো ঊনিশ "
        + "বিশ একুশ বাইশ তেইশ চব্বিশ পঁচিশ ছাব্বিশ সাতাশ আঠাশ ঊনত্রিশ ত্রিশ একত্রিশ বত্রিশ তেত্রিশ চৌত্রিশ পঁয়ত্রিশ ছত্রিশ সাঁইত্রিশ আটত্রিশ ঊনচল্লিশ "
        + "চল্লিশ একচল্লিশ বিয়াল্লিশ তেতাল্লিশ চুয়াল্লিশ পঁয়তাল্লিশ ছেচল্লিশ সাতচল্লিশ আটচল্লিশ ঊনপঞ্চাশ পঞ্চাশ একান্ন বাহান্ন তিপ্পান্ন চুয়ান্ন পঞ্চান্ন ছাপ্পান্ন সাতান্ন আটান্ন ঊনষাট "
        + "ষাট একষট্টি বাষট্টি তেষট্টি চৌষট্টি পঁয়ষট্টি ছেষট্টি সাতষট্টি আটষট্টি ঊনসত্তর সত্তর একাত্তর বাহাত্তর তিয়াত্তর চুয়াত্তর পঁচাত্তর ছিয়াত্তর সাতাত্তর আটাত্তর ঊনআশি "
        + "আশি একাশি বিরাশি তিরাশি চুরাশি পঁচাশি ছিয়াশি সাতাশি আটাশি ঊননব্বই নব্বই একানব্বই বিরানব্বই তিরানব্বই চুরানব্বই পঁচানব্বই ছিয়ানব্বই সাতানব্বই আটানব্বই নিরানব্বই").Split(' ');

    private static readonly string[] Ones = ["", "One", "Two", "Three", "Four", "Five", "Six", "Seven", "Eight", "Nine", "Ten", "Eleven", "Twelve", "Thirteen", "Fourteen",
        "Fifteen", "Sixteen", "Seventeen", "Eighteen", "Nineteen"];

    private static readonly string[] Tens = ["", "", "Twenty", "Thirty", "Forty", "Fifty", "Sixty", "Seventy", "Eighty", "Ninety"];

    public static string Digits(string s, bool bn) =>
        !bn ? s : string.Create(s.Length, s, (span, src) => { for (int i = 0; i < src.Length; i++) span[i] = src[i] is >= '0' and <= '9' ? BnDigits[src[i] - '0'] : src[i]; });

    public static string Int(long n, bool bn) => Digits(n.ToString(CultureInfo.InvariantCulture), bn);

    public static string Percent(decimal rate, bool bn) => Digits((rate * 100).ToString("0.##", CultureInfo.InvariantCulture) + "%", bn);

    /// <summary>12,345.60 (English: thousands) or ১২,৩৪৫.৬০ (Bangla: lakh grouping, as <c>bn-BD</c>).</summary>
    public static string Money(long minor, bool bn)
    {
        long abs = Math.Abs(minor);
        string whole = (abs / 100).ToString(CultureInfo.InvariantCulture);
        var sb = new StringBuilder();
        for (int i = 0; i < whole.Length; i++)
        {
            int fromEnd = whole.Length - i;
            sb.Append(whole[i]);
            bool comma = fromEnd > 1 && (bn ? fromEnd == 4 || (fromEnd > 4 && fromEnd % 2 == 0) : fromEnd % 3 == 1);
            if (comma) sb.Append(',');
        }
        return Digits((minor < 0 ? "-" : "") + sb + "." + (abs % 100).ToString("00", CultureInfo.InvariantCulture), bn);
    }

    public static string Date(DateOnly d, bool bn) =>
        bn ? $"{Int(d.Day, true)} {BnMonths[d.Month - 1]} {Int(d.Year, true)}" : d.ToString("d MMM yyyy", CultureInfo.InvariantCulture);

    /// <summary>"Taka … and … Paisa Only" / "টাকা … মাত্র" for an amount in poisha.</summary>
    public static string Words(long minor, bool bn)
    {
        long taka = Math.Abs(minor) / 100, paisa = Math.Abs(minor) % 100;
        if (bn)
            return "টাকা " + BnWords(taka) + (paisa > 0 ? " এবং " + Bn99[paisa] + " পয়সা" : "") + " মাত্র";
        return "Taka " + EnWords(taka) + (paisa > 0 ? " and " + EnBelow100(paisa) + " Paisa" : "") + " Only";
    }

    private static string EnBelow100(long n) => n < 20 ? Ones[n] : Tens[n / 10] + (n % 10 > 0 ? " " + Ones[n % 10] : "");

    private static string EnBelow1000(long n) => n >= 100 ? Ones[n / 100] + " Hundred" + (n % 100 > 0 ? " " + EnBelow100(n % 100) : "") : EnBelow100(n);

    private static string EnWords(long n)
    {
        if (n == 0) return "Zero";
        var parts = new List<string>();
        if (n / 10_000_000 > 0) parts.Add(EnWords(n / 10_000_000) + " Crore");
        if (n / 100_000 % 100 > 0) parts.Add(EnBelow100(n / 100_000 % 100) + " Lakh");
        if (n / 1_000 % 100 > 0) parts.Add(EnBelow100(n / 1_000 % 100) + " Thousand");
        if (n % 1_000 > 0) parts.Add(EnBelow1000(n % 1_000));
        return string.Join(' ', parts);
    }

    private static string BnBelow1000(long n) => n >= 100 ? Bn99[n / 100] + "শ" + (n % 100 > 0 ? " " + Bn99[n % 100] : "") : Bn99[n];

    private static string BnWords(long n)
    {
        if (n == 0) return Bn99[0];
        var parts = new List<string>();
        if (n / 10_000_000 > 0) parts.Add(BnWords(n / 10_000_000) + " কোটি");
        if (n / 100_000 % 100 > 0) parts.Add(Bn99[n / 100_000 % 100] + " লাখ");
        if (n / 1_000 % 100 > 0) parts.Add(Bn99[n / 1_000 % 100] + " হাজার");
        if (n % 1_000 > 0) parts.Add(BnBelow1000(n % 1_000));
        return string.Join(' ', parts);
    }
}
