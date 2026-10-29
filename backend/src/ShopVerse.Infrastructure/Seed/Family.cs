using System.Globalization;
using System.Text.RegularExpressions;

namespace ShopVerse.Infrastructure.Seed;

/// <summary>Illustration silhouette used when generating product artwork.</summary>
internal enum Shape { DinModule, Panel, RoundSensor, BlockSensor, Cylinder, Motor, Valve, Cable, Enclosure, Meter, Pump, Gauge, Pushbutton, Fitting }

/// <summary>One purchasable option axis, e.g. Coil voltage with a price factor per value.</summary>
internal sealed record AxisSeed(string Name, (string Value, decimal Factor)[] Options)
{
    /// <summary>Parses <c>"Coil voltage|24 V DC:1.00;230 V AC:1.02"</c>.</summary>
    public static AxisSeed Parse(string text)
    {
        var parts = text.Split('|', 2);
        var options = parts[1].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(o =>
            {
                var idx = o.LastIndexOf(':');
                return (o[..idx].Trim(), decimal.Parse(o[(idx + 1)..], CultureInfo.InvariantCulture));
            }).ToArray();
        return new AxisSeed(parts[0].Trim(), options);
    }
}

/// <summary>
/// Table-driven product family: a header, N model rows and templates expanding to titles, specs,
/// highlights and descriptions. Rows are pipe-separated; <c>{Header}</c> tokens expand per row.
/// </summary>
internal sealed class Family
{
    private static readonly Regex Token = new(@"\{([^}]+)\}", RegexOptions.Compiled);

    public required string Leaf { get; init; }
    public required string Brand { get; init; }
    public required string Series { get; init; }
    public required string NounEn { get; init; }
    public required string NounBn { get; init; }
    public required string Header { get; init; }
    public required string[] Rows { get; init; }
    public required string Title { get; init; }
    public required string Intro { get; init; }
    public string[] Highlights { get; init; } = [];
    public string[] Applications { get; init; } = [];

    /// <summary>"Group:HeaderKey=Spec label;OtherKey=Label"</summary>
    public string[] Specs { get; init; } = [];

    /// <summary>"Group:Label=Value"</summary>
    public string[] Fixed { get; init; } = [];

    public string[] Axes { get; init; } = [];
    public Shape Shape { get; init; } = Shape.DinModule;
    public int WarrantyMonths { get; init; } = 12;
    public decimal WeightKg { get; init; } = 0.4m;
    public (decimal L, decimal W, decimal H) BoxCm { get; init; } = (12m, 9m, 7m);

    /// <summary>Relative demand, drives sold counts and review volume.</summary>
    public double Popularity { get; init; } = 1.0;

    /// <summary>Spec labels to register as filterable attributes (Phase 5 facets).</summary>
    public string[] FilterSpecs { get; init; } = [];

    public IReadOnlyList<Dictionary<string, string>> ParsedRows()
    {
        var header = Header.Split('|');
        return Rows.Select(r =>
        {
            var cells = r.Split('|');
            if (cells.Length != header.Length)
                throw new InvalidOperationException($"{Brand} {Series}: row '{r}' has {cells.Length} cells, header has {header.Length}.");
            var map = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 0; i < header.Length; i++) map[header[i]] = cells[i].Trim();
            return map;
        }).ToList();
    }

    public string Expand(string template, IReadOnlyDictionary<string, string> row) =>
        Token.Replace(template, m => m.Groups[1].Value switch
        {
            "Brand" => Brand,
            "Series" => Series,
            "NounEn" => NounEn,
            var key => row.TryGetValue(key, out var v) ? v
                : throw new InvalidOperationException($"{Brand} {Series}: unknown token {{{key}}} in '{template}'.")
        });

    public IReadOnlyList<AxisSeed> ParsedAxes() => Axes.Select(AxisSeed.Parse).ToList();
}
