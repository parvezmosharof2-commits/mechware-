using System.Globalization;
using System.Security.Cryptography;
using Mechware.Domain.Orders;

namespace Mechware.Domain.Quotes;

/// <summary>
/// <c>MWQ-YYMMDD-XXXXXX</c> (quotes) and <c>MWR-YYMMDD-XXXXXX</c> (RFQs): the <see cref="OrderNumber"/> scheme, UTC date and six
/// random base32 characters from a cryptographic generator (ADR-0011 §3, §15 decision 6; the brief's four could be guessed).
/// </summary>
public static class QuoteNumber
{
    public const string QuotePrefix = "MWQ", RfqPrefix = "MWR";
    public const int Length = 17;

    public static string NewQuote(DateTime nowUtc, Func<int, int>? randomIndex = null) => Generate(QuotePrefix, nowUtc, randomIndex);
    public static string NewRfq(DateTime nowUtc, Func<int, int>? randomIndex = null) => Generate(RfqPrefix, nowUtc, randomIndex);
    public static bool IsQuoteNumber(string? value) => IsValid(value, QuotePrefix);
    public static bool IsRfqNumber(string? value) => IsValid(value, RfqPrefix);

    private static string Generate(string prefix, DateTime nowUtc, Func<int, int>? randomIndex)
    {
        randomIndex ??= RandomNumberGenerator.GetInt32;
        var suffix = string.Create(6, randomIndex, static (span, rnd) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = OrderNumber.Alphabet[rnd(OrderNumber.Alphabet.Length)];
        });
        return string.Create(CultureInfo.InvariantCulture, $"{prefix}-{nowUtc:yyMMdd}-{suffix}");
    }

    private static bool IsValid(string? value, string prefix) =>
        value is { Length: Length }
        && value.StartsWith(prefix + "-", StringComparison.Ordinal)
        && DateOnly.TryParseExact(value.AsSpan(4, 6), "yyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
        && value.AsSpan(4, 6).IndexOfAnyExceptInRange('0', '9') < 0
        && value[10] == '-'
        && value.AsSpan(11).IndexOfAnyExcept(OrderNumber.Alphabet) < 0;
}
