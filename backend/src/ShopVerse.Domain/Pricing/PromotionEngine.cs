using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Domain.Pricing;

public enum PromotionKind
{
    /// <summary>Percentage off the eligible lines.</summary>
    PercentOff = 1,

    /// <summary>A fixed amount off, never more than the eligible lines are worth.</summary>
    FixedOff = 2,

    /// <summary>Per line: the highest quantity break the line reaches gives its percentage.</summary>
    VolumeTier = 3,

    /// <summary>Whole eligible basket: the highest spend threshold reached gives its percentage.</summary>
    SpendTier = 4
}

public enum PromotionScopeKind { All = 1, Category = 2, Seller = 3, Variant = 4 }

/// <param name="TargetId">Category, seller or variant id; null only for <see cref="PromotionScopeKind.All"/>.</param>
public sealed record PromotionScope(PromotionScopeKind Kind, Guid? TargetId = null)
{
    public static PromotionScope Everything { get; } = new(PromotionScopeKind.All);
}

public sealed record VolumeBreak(int MinQuantity, decimal Percent);

public sealed record SpendBreak(long MinSubtotalMinor, decimal Percent);

/// <summary>
/// A promotion as configured by staff. All money is in minor units (poisha). Whether it came from a coupon code or is
/// automatic is the caller's business: only the promotions passed to the engine are considered.
/// </summary>
public sealed record Promotion(
    Guid Id,
    string Name,
    PromotionKind Kind,
    PromotionScope Scope,
    int Priority = 100,
    bool Exclusive = false,
    decimal Percent = 0m,
    long FixedMinor = 0,
    long? MaxDiscountMinor = null,
    long MinSubtotalMinor = 0,
    IReadOnlyList<VolumeBreak>? VolumeBreaks = null,
    IReadOnlyList<SpendBreak>? SpendBreaks = null,
    DateTime? StartsAtUtc = null,
    DateTime? EndsAtUtc = null,
    int? GlobalLimit = null,
    int? PerUserLimit = null);

/// <summary>How often a promotion has already been used; supplied by the caller, who owns the counters.</summary>
public sealed record PromotionUsage(int GlobalUses, int UserUses);

/// <param name="UnitNetMinor">Unit price after price resolution (brief §8.1), before any promotion.</param>
public sealed record CartLine(Guid LineId, Guid VariantId, Guid SellerId, Guid CategoryId, int Quantity, long UnitNetMinor)
{
    public long GrossMinor => checked(Quantity * UnitNetMinor);
}

public sealed record LineDiscount(Guid LineId, long DiscountMinor);

public sealed record AppliedPromotion(Guid PromotionId, string Name, long DiscountMinor);

public sealed record RejectedPromotion(Guid PromotionId, string Reason);

public sealed record PromotionResult(
    IReadOnlyList<LineDiscount> Lines,
    IReadOnlyList<AppliedPromotion> Applied,
    IReadOnlyList<RejectedPromotion> Rejected,
    long TotalDiscountMinor);

/// <summary>
/// Deterministic, pure promotion pricing (Phase 8). Promotions apply one after another in (priority, id) order, each on
/// what the earlier ones left, so the order of the input list never changes the answer. A discount is rounded once
/// (banker's rounding, ADR-0002) and then <b>allocated</b> to lines by largest remainder: the line discounts always add
/// up to exactly the promotion's discount, so invoices and refunds never lose or invent a poisha.
/// </summary>
public static class PromotionEngine
{
    public const string NotYetValid = "NOT_YET_VALID";
    public const string Expired = "EXPIRED";
    public const string GlobalLimitReached = "GLOBAL_LIMIT_REACHED";
    public const string PerUserLimitReached = "PER_USER_LIMIT_REACHED";
    public const string NoEligibleLines = "NO_ELIGIBLE_LINES";
    public const string BelowMinimumSpend = "BELOW_MINIMUM_SPEND";
    public const string NoEffect = "NO_EFFECT";
    public const string NotStackable = "NOT_STACKABLE";

    public static PromotionResult Apply(
        IReadOnlyList<CartLine> lines,
        IReadOnlyList<Promotion> promotions,
        DateTime asOfUtc,
        IReadOnlyDictionary<Guid, PromotionUsage>? usage = null)
    {
        foreach (var line in lines)
        {
            if (line.Quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
            if (line.UnitNetMinor < 0) throw new ValidationException("unitPrice", "Unit price cannot be negative.");
        }

        var remaining = lines.Select(l => l.GrossMinor).ToArray();
        var perLine = new long[lines.Count];
        var applied = new List<AppliedPromotion>();
        var rejected = new List<RejectedPromotion>();

        foreach (var promo in promotions.OrderBy(p => p.Priority).ThenBy(p => p.Id))
        {
            if (Reject(promo, asOfUtc, usage) is { } reason) { rejected.Add(new(promo.Id, reason)); continue; }
            if (applied.Count > 0 && (promo.Exclusive || applied.Any(a => IsExclusive(promotions, a.PromotionId))))
            {
                rejected.Add(new(promo.Id, NotStackable));
                continue;
            }

            var eligible = Enumerable.Range(0, lines.Count).Where(i => InScope(promo.Scope, lines[i])).ToArray();
            if (eligible.Length == 0) { rejected.Add(new(promo.Id, NoEligibleLines)); continue; }

            // Thresholds look at what the buyer is actually buying, not at what earlier promotions left over.
            var originalSpend = eligible.Sum(i => lines[i].GrossMinor);
            if (originalSpend < promo.MinSubtotalMinor) { rejected.Add(new(promo.Id, BelowMinimumSpend)); continue; }

            var wanted = Wanted(promo, lines, remaining, eligible, originalSpend);
            var available = eligible.Sum(i => remaining[i]);
            var total = Math.Min(wanted.Sum(), available);
            if (promo.MaxDiscountMinor is { } cap) total = Math.Min(total, cap);
            if (total <= 0) { rejected.Add(new(promo.Id, NoEffect)); continue; }

            // Split the capped total in proportion to what each line would have received, bounded by what it has left.
            var shares = Allocate(total, wanted, eligible.Select(i => remaining[i]).ToArray());
            for (var k = 0; k < eligible.Length; k++)
            {
                remaining[eligible[k]] -= shares[k];
                perLine[eligible[k]] += shares[k];
            }

            applied.Add(new(promo.Id, promo.Name, total));
        }

        var lineDiscounts = lines.Select((l, i) => new LineDiscount(l.LineId, perLine[i])).ToList();
        return new PromotionResult(lineDiscounts, applied, rejected, perLine.Sum());
    }

    private static bool IsExclusive(IReadOnlyList<Promotion> all, Guid id) => all.First(p => p.Id == id).Exclusive;

    private static string? Reject(Promotion p, DateTime asOf, IReadOnlyDictionary<Guid, PromotionUsage>? usage)
    {
        if (p.StartsAtUtc is { } s && asOf < s) return NotYetValid;
        if (p.EndsAtUtc is { } e && asOf >= e) return Expired;
        var used = usage is not null && usage.TryGetValue(p.Id, out var u) ? u : new PromotionUsage(0, 0);
        if (p.GlobalLimit is { } g && used.GlobalUses >= g) return GlobalLimitReached;
        if (p.PerUserLimit is { } pu && used.UserUses >= pu) return PerUserLimitReached;
        return null;
    }

    private static bool InScope(PromotionScope scope, CartLine line) => scope.Kind switch
    {
        PromotionScopeKind.All => true,
        PromotionScopeKind.Category => line.CategoryId == scope.TargetId,
        PromotionScopeKind.Seller => line.SellerId == scope.TargetId,
        PromotionScopeKind.Variant => line.VariantId == scope.TargetId,
        _ => false
    };

    /// <summary>What each eligible line would get on its own, before the caps. Index matches <paramref name="eligible"/>.</summary>
    private static long[] Wanted(Promotion p, IReadOnlyList<CartLine> lines, long[] remaining, int[] eligible, long originalSpend)
    {
        switch (p.Kind)
        {
            case PromotionKind.VolumeTier:
                return eligible.Select(i =>
                {
                    var brk = (p.VolumeBreaks ?? []).Where(b => lines[i].Quantity >= b.MinQuantity).OrderByDescending(b => b.MinQuantity).FirstOrDefault();
                    return brk is null ? 0 : Percent(remaining[i], brk.Percent);
                }).ToArray();

            case PromotionKind.SpendTier:
                {
                    var brk = (p.SpendBreaks ?? []).Where(b => originalSpend >= b.MinSubtotalMinor).OrderByDescending(b => b.MinSubtotalMinor).FirstOrDefault();
                    return Proportional(brk is null ? 0 : Percent(eligible.Sum(i => remaining[i]), brk.Percent), eligible, remaining);
                }

            case PromotionKind.PercentOff:
                return Proportional(Percent(eligible.Sum(i => remaining[i]), p.Percent), eligible, remaining);

            case PromotionKind.FixedOff:
                return Proportional(p.FixedMinor, eligible, remaining);

            default:
                throw new ValidationException("kind", $"Unknown promotion kind {p.Kind}.");
        }
    }

    private static long[] Proportional(long total, int[] eligible, long[] remaining) =>
        Allocate(Math.Max(0, Math.Min(total, eligible.Sum(i => remaining[i]))), eligible.Select(i => remaining[i]).ToArray(), eligible.Select(i => remaining[i]).ToArray());

    private static long Percent(long amount, decimal percent)
    {
        if (percent is < 0m or > 100m) throw new ValidationException("percent", "A percentage must be between 0 and 100.");
        return (long)decimal.Round(amount * percent / 100m, 0, MidpointRounding.ToEven);
    }

    /// <summary>
    /// Splits <paramref name="total"/> over the entries in proportion to <paramref name="weights"/> by the largest
    /// remainder method (ties go to the earlier entry), never giving an entry more than its <paramref name="limits"/>.
    /// The shares add up to <paramref name="total"/> exactly, or to the sum of the limits if that is smaller.
    /// </summary>
    public static long[] Allocate(long total, long[] weights, long[] limits)
    {
        var n = weights.Length;
        var shares = new long[n];
        var weightSum = weights.Sum();
        if (total <= 0 || weightSum <= 0) return shares;

        var remainders = new decimal[n];
        long given = 0;
        for (var i = 0; i < n; i++)
        {
            var exact = (decimal)total * weights[i] / weightSum;
            shares[i] = Math.Min((long)decimal.Floor(exact), limits[i]);
            remainders[i] = exact - decimal.Floor(exact);
            given += shares[i];
        }

        // Hand out the leftover one poisha at a time to the largest remainders that still have room.
        var left = total - given;
        foreach (var i in Enumerable.Range(0, n).OrderByDescending(i => remainders[i]).ThenBy(i => i))
        {
            if (left == 0) break;
            if (shares[i] < limits[i] && weights[i] > 0) { shares[i]++; left--; }
        }

        // Anything still left means rounding pushed past the limits; spread it where room remains.
        for (var i = 0; left > 0 && i < n; i++)
        {
            var room = Math.Min(left, limits[i] - shares[i]);
            shares[i] += room;
            left -= room;
        }

        return shares;
    }
}
