namespace ShopVerse.UnitTests.Properties;

/// <summary>
/// Minimal deterministic property runner: every run uses fixed seeds, so a failure is reproducible
/// from the seed and iteration printed in the message.
/// </summary>
internal static class PropertyRunner
{
    public const int DefaultIterations = 5_000;

    public static void ForAll(int seed, int iterations, Action<Random, int> property)
    {
        var rng = new Random(seed);
        for (var i = 0; i < iterations; i++)
        {
            try
            {
                property(rng, i);
            }
            catch (Exception ex)
            {
                throw new Xunit.Sdk.XunitException($"Property failed (seed={seed}, iteration={i}): {ex.Message}");
            }
        }
    }

    /// <summary>Amount with exactly two decimals in [-max, max].</summary>
    public static decimal Amount(this Random rng, long maxMinor = 1_000_000_000_00L, bool allowNegative = true)
    {
        var minor = rng.NextInt64(allowNegative ? -maxMinor : 0, maxMinor + 1);
        return minor / 100m;
    }

    /// <summary>Arbitrary decimal with up to 6 fractional digits (exercises rounding).</summary>
    public static decimal RawDecimal(this Random rng, long maxWhole = 10_000_000)
    {
        var whole = rng.NextInt64(-maxWhole, maxWhole + 1);
        var frac = rng.Next(0, 1_000_000) / 1_000_000m;
        return whole + (whole < 0 ? -frac : frac);
    }

    private const string Pool =
        "abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789 -_./\\:;'\"<>&%#?=+*()[]{}!@$^|~`" +
        "\t\r\n\u0000\u0007\u200B\u200E\u202E\u2028\uFEFF" +
        "ÄÖÜßéèçñøåæœ" +
        "বাংলাদেশমেকওয়্যারপিএলসি" +
        "日本語中文한국어Ελληνικάрусский" +
        "😀🔧⚙️🏭";

    public static string NastyString(this Random rng, int maxLength = 80)
    {
        var len = rng.Next(0, maxLength + 1);
        var chars = new char[len];
        for (var i = 0; i < len; i++) chars[i] = Pool[rng.Next(Pool.Length)];
        return new string(chars);
    }
}
