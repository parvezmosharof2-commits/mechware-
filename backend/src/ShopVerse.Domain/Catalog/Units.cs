using System.Globalization;
using System.Text.RegularExpressions;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Domain.Catalog;

/// <summary>
/// The units technical attributes may use, and conversion between units of the same quantity (PIM). A value is read from
/// what a seller writes ("0.75 kW (1 HP)", "750 W", "0–10 bar") and stored in the attribute's unit, so values written in
/// different units compare and filter.
/// </summary>
public static partial class UnitCatalog
{
    /// <summary>Largest value accepted in any unit; anything above is a typo, not a real part.</summary>
    public const decimal MaxValue = 1_000_000_000m;

    /// <summary>Longest text read; attribute values are stored in at most this many characters.</summary>
    public const int MaxTextLength = 150;

    private sealed record UnitDef(string Symbol, string Quantity, decimal ToBase);

    private static readonly UnitDef[] Units =
    [
        new("W", "power", 1m), new("kW", "power", 1_000m), new("HP", "power", 745.7m),
        new("V", "voltage", 1m), new("kV", "voltage", 1_000m),
        new("mA", "current", 0.001m), new("A", "current", 1m),
        new("mm", "length", 1m), new("cm", "length", 10m), new("m", "length", 1_000m),
        new("kPa", "pressure", 1_000m), new("bar", "pressure", 100_000m), new("MPa", "pressure", 1_000_000m),
        new("psi", "pressure", 6_894.757m),
        new("Hz", "frequency", 1m), new("kHz", "frequency", 1_000m),
        new("%", "ratio", 1m)
    ];

    /// <summary>True for a unit symbol this catalogue knows, written exactly (e.g. "kW", not "KW").</summary>
    public static bool IsKnown(string? symbol) => symbol is not null && Units.Any(u => u.Symbol == symbol);

    /// <summary>
    /// Reads the first number (or "low–high" range) followed by a unit from <paramref name="text"/> and converts it to
    /// <paramref name="targetUnit"/>. A single value gives min = max. Throws <see cref="ValidationException"/> when there is
    /// no such number, the unit is unknown or measures something else, the range runs backwards, or the value is absurd.
    /// </summary>
    public static (decimal Min, decimal Max) Parse(string text, string targetUnit, bool allowRange)
    {
        var target = Find(targetUnit) ?? throw new ValidationException("unit", $"Unknown unit '{targetUnit}'.");
        if (text is null || text.Length > MaxTextLength)
            throw new ValidationException("value", $"A value is at most {MaxTextLength} characters.");
        MatchCollection matches;
        try
        {
            matches = ValueWithUnit().Matches(text);
            _ = matches.Count;   // runs the whole match now, inside the try
        }
        catch (RegexMatchTimeoutException)
        {
            throw new ValidationException("value", "The value could not be read.");
        }

        foreach (Match m in matches)
        {
            var unit = Find(m.Groups["unit"].Value);
            if (unit is null) continue;   // e.g. "1-phase": a number, but no unit after it
            if (unit.Quantity != target.Quantity)
                throw new ValidationException("value", $"'{text}' is a {unit.Quantity}, not a {target.Quantity}.");

            decimal low = Convert(m.Groups["low"].Value, unit, target);
            decimal high = m.Groups["high"].Success ? Convert(m.Groups["high"].Value, unit, target) : low;
            if (high != low && !allowRange)
                throw new ValidationException("value", $"'{text}' is a range; this attribute takes a single value.");
            if (high < low) throw new ValidationException("value", $"'{text}' runs from high to low.");
            if (high > MaxValue) throw new ValidationException("value", $"'{text}' is out of range.");
            return (low, high);
        }

        throw new ValidationException("value", $"'{text}' does not contain a value in {target.Symbol}.");
    }

    private static decimal Convert(string number, UnitDef from, UnitDef to)
    {
        if (!decimal.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var value))
            throw new ValidationException("value", $"'{number}' is not a number.");
        return value <= MaxValue ? Math.Round(value * from.ToBase / to.ToBase, 6) : decimal.MaxValue;
    }

    /// <summary>Exact symbol first; otherwise a case-insensitive match, but only when it is unambiguous ("KW" → kW).</summary>
    private static UnitDef? Find(string symbol)
    {
        var exact = Units.FirstOrDefault(u => u.Symbol == symbol);
        if (exact is not null) return exact;
        var loose = Units.Where(u => string.Equals(u.Symbol, symbol, StringComparison.OrdinalIgnoreCase)).ToArray();
        return loose.Length == 1 ? loose[0] : null;
    }

    // "0.75 kW", "0–10 bar", "85-264 V", "91.4 %": a number (ASCII digits only), an optional "–high", then the unit token.
    // The unit must end there: "mm²" is an area and "kWh" an energy, never mm or kW.
    [GeneratedRegex(@"(?<![0-9.])(?<low>[0-9]{1,12}(?:\.[0-9]{1,6})?)(?:\s*[–-]\s*(?<high>[0-9]{1,12}(?:\.[0-9]{1,6})?))?\s*(?<unit>%|[A-Za-z]+)(?![\w²³])", RegexOptions.CultureInvariant, matchTimeoutMilliseconds: 100)]
    private static partial Regex ValueWithUnit();
}
