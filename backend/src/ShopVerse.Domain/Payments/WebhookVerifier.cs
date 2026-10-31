using System.Security.Cryptography;
using System.Text;

namespace ShopVerse.Domain.Payments;

public enum WebhookVerdict
{
    Accepted = 1,
    MissingSignature = 2,
    BadSignature = 3,
    MissingTimestamp = 4,
    Expired = 5,
    FromTheFuture = 6
}

/// <summary>
/// Verifies a payment-provider webhook (brief §8.7: signature-verified, time-windowed). Pure: the caller dedupes the
/// event id afterwards through the inbox, because "have I seen this id" needs storage and this does not.
/// The signed message is <c>{timestampUnixSeconds}.{rawBody}</c> hashed with HMAC-SHA256 and the shared secret; the
/// signature header is lower-case hex, optionally prefixed with <c>sha256=</c>. The timestamp is part of the signed
/// message, so a captured request cannot be replayed with a fresh timestamp.
/// </summary>
public static class WebhookVerifier
{
    public static readonly TimeSpan DefaultTolerance = TimeSpan.FromMinutes(5);

    public static string Sign(string secret, long timestampUnixSeconds, string rawBody)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var message = Encoding.UTF8.GetBytes($"{timestampUnixSeconds}.{rawBody}");
        return Convert.ToHexStringLower(HMACSHA256.HashData(key, message));
    }

    public static WebhookVerdict Verify(
        string secret, string? signatureHeader, string? timestampHeader, string rawBody, DateTime nowUtc, TimeSpan? tolerance = null)
    {
        if (string.IsNullOrWhiteSpace(secret)) throw new ArgumentException("A webhook secret is required.", nameof(secret));
        if (string.IsNullOrWhiteSpace(signatureHeader)) return WebhookVerdict.MissingSignature;
        if (!long.TryParse(timestampHeader, out var timestamp) || timestamp <= 0) return WebhookVerdict.MissingTimestamp;

        var provided = signatureHeader.Trim();
        if (provided.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)) provided = provided[7..];
        provided = provided.ToLowerInvariant();

        // Signature first: nothing about timing is revealed to a caller that cannot sign.
        var expected = Sign(secret, timestamp, rawBody);
        var ok = provided.Length == expected.Length
                 && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(provided), Encoding.ASCII.GetBytes(expected));
        if (!ok) return WebhookVerdict.BadSignature;

        var sent = DateTimeOffset.FromUnixTimeSeconds(timestamp).UtcDateTime;
        var window = tolerance ?? DefaultTolerance;
        if (sent > nowUtc + window) return WebhookVerdict.FromTheFuture;
        if (nowUtc - sent > window) return WebhookVerdict.Expired;
        return WebhookVerdict.Accepted;
    }
}
