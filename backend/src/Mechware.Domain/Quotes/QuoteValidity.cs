namespace Mechware.Domain.Quotes;

/// <summary>
/// Validity of a sent version (ADR-0011 §7, threat model QT5). It starts at the instant t the version is sent (direct send or
/// approval). The last valid day is D = DhakaDate(t) + days; the version is valid until the start of D + 1 in Asia/Dhaka (a
/// fixed +06:00, no daylight saving, as E8.3 and B3), exclusive, with no grace. Expiry is computed from the clock, never stored.
/// </summary>
public static class QuoteValidity
{
    public static readonly TimeSpan DhakaOffset = TimeSpan.FromHours(6);

    public static DateOnly DhakaDate(DateTime utc) => DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc) + DhakaOffset);

    /// <summary>The date the PDF prints as "valid until".</summary>
    public static DateOnly LastValidDhakaDate(DateTime sentAtUtc, int validityDays) => DhakaDate(sentAtUtc).AddDays(validityDays);

    public static DateTime ValidUntilUtc(DateTime sentAtUtc, int validityDays) =>
        DateTime.SpecifyKind(LastValidDhakaDate(sentAtUtc, validityDays).AddDays(1).ToDateTime(TimeOnly.MinValue) - DhakaOffset, DateTimeKind.Utc);

    /// <summary>The single rule every acceptance uses: <c>now &lt; valid_until</c>.</summary>
    public static bool IsValidAt(DateTime validUntilUtc, DateTime nowUtc) => nowUtc < validUntilUtc;
}
