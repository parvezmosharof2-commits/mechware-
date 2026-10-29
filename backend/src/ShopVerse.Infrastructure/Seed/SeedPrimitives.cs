using System.Security.Cryptography;
using System.Text;

namespace ShopVerse.Infrastructure.Seed;

/// <summary>Stable GUIDs derived from a name, so re-seeding produces identical ids (idempotent, diff-friendly).</summary>
internal static class DeterministicGuid
{
    public static Guid From(string name)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes("mechware-seed:" + name));
        Span<byte> bytes = stackalloc byte[16];
        hash.AsSpan(0, 16).CopyTo(bytes);
        bytes[6] = (byte)((bytes[6] & 0x0F) | 0x50);   // version 5
        bytes[8] = (byte)((bytes[8] & 0x3F) | 0x80);   // RFC 4122 variant
        return new Guid(bytes);
    }
}

/// <summary>Thin deterministic RNG wrapper (fixed seed → identical dataset on every run).</summary>
internal sealed class SeedRandom
{
    private readonly Random _rng;

    public SeedRandom(int seed) => _rng = new Random(seed);

    public int Next(int minInclusive, int maxExclusive) => _rng.Next(minInclusive, maxExclusive);
    public double NextDouble() => _rng.NextDouble();
    public bool Chance(double probability) => _rng.NextDouble() < probability;

    public T Pick<T>(IReadOnlyList<T> items) => items[_rng.Next(items.Count)];

    public T PickWeighted<T>(IReadOnlyList<(T Item, double Weight)> items)
    {
        var total = items.Sum(i => i.Weight);
        var roll = _rng.NextDouble() * total;
        foreach (var (item, weight) in items)
        {
            roll -= weight;
            if (roll <= 0) return item;
        }

        return items[^1].Item;
    }

    public IReadOnlyList<T> Sample<T>(IReadOnlyList<T> items, int count)
    {
        var copy = items.ToList();
        var result = new List<T>(Math.Min(count, copy.Count));
        for (var i = 0; i < count && copy.Count > 0; i++)
        {
            var idx = _rng.Next(copy.Count);
            result.Add(copy[idx]);
            copy.RemoveAt(idx);
        }

        return result;
    }

    /// <summary>Rounds a price to a "shelf" value: whole taka, ending in 0/50/90/00 depending on magnitude.</summary>
    public static decimal ShelfPrice(decimal raw) => raw switch
    {
        < 100m => Math.Round(raw, 0),
        < 1000m => Math.Round(raw / 5m, 0) * 5m,
        < 10_000m => Math.Round(raw / 10m, 0) * 10m,
        < 100_000m => Math.Round(raw / 50m, 0) * 50m,
        _ => Math.Round(raw / 500m, 0) * 500m
    };
}
