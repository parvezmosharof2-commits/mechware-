using System.Text.Json;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Pricing;

/// <summary>
/// A promotion as staff configure and store it. A promotion with a <see cref="Code"/> applies only when the buyer enters
/// that code; one without applies automatically while active. The pure arithmetic lives in <see cref="PromotionEngine"/>;
/// this entity only holds the configuration and the redemption counters the usage limits are enforced against.
/// </summary>
public sealed class PromotionDefinition : Entity<Guid>
{
    public string Name { get; private set; } = string.Empty;

    /// <summary>Stored upper-case; null for an automatic promotion.</summary>
    public string? Code { get; private set; }
    public PromotionKind Kind { get; private set; }
    public PromotionScopeKind ScopeKind { get; private set; }
    public Guid? ScopeTargetId { get; private set; }
    public int Priority { get; private set; } = 100;
    public bool Exclusive { get; private set; }
    public decimal Percent { get; private set; }
    public long FixedMinor { get; private set; }
    public long? MaxDiscountMinor { get; private set; }
    public long MinSubtotalMinor { get; private set; }
    public string VolumeBreaksJson { get; private set; } = "[]";
    public string SpendBreaksJson { get; private set; } = "[]";
    public DateTime? StartsAtUtc { get; private set; }
    public DateTime? EndsAtUtc { get; private set; }
    public int? GlobalLimit { get; private set; }
    public int? PerUserLimit { get; private set; }
    public bool IsActive { get; private set; } = true;

    /// <summary>How many orders currently hold a use of this promotion. Changed only by atomic SQL, never read-modify-write.</summary>
    public int RedeemedCount { get; private set; }

    private PromotionDefinition() { }

    public static PromotionDefinition Create(
        Guid id, string name, string? code, PromotionKind kind, PromotionScope scope, int priority = 100, bool exclusive = false,
        decimal percent = 0m, long fixedMinor = 0, long? maxDiscountMinor = null, long minSubtotalMinor = 0,
        IReadOnlyList<VolumeBreak>? volumeBreaks = null, IReadOnlyList<SpendBreak>? spendBreaks = null,
        DateTime? startsAtUtc = null, DateTime? endsAtUtc = null, int? globalLimit = null, int? perUserLimit = null)
    {
        if (string.IsNullOrWhiteSpace(name)) throw new ValidationException("name", "A promotion needs a name.");
        if (code is not null && !System.Text.RegularExpressions.Regex.IsMatch(code.Trim(), "^[A-Za-z0-9_-]{3,40}$"))
            throw new ValidationException("code", "A code is 3 to 40 letters, digits, '-' or '_'.");
        if (percent is < 0m or > 100m) throw new ValidationException("percent", "A percentage must be between 0 and 100.");
        if (fixedMinor < 0 || minSubtotalMinor < 0 || maxDiscountMinor < 0) throw new ValidationException("amount", "Amounts cannot be negative.");
        if (globalLimit is < 1 || perUserLimit is < 1) throw new ValidationException("limit", "A usage limit must be at least 1.");
        if (startsAtUtc is { } s && endsAtUtc is { } e && e <= s) throw new ValidationException("window", "The end must be after the start.");
        if (scope.Kind != PromotionScopeKind.All && scope.TargetId is null) throw new ValidationException("scope", "This scope needs a target.");
        if (kind == PromotionKind.VolumeTier && (volumeBreaks is null || volumeBreaks.Count == 0)) throw new ValidationException("volumeBreaks", "A volume tier needs at least one break.");
        if (kind == PromotionKind.SpendTier && (spendBreaks is null || spendBreaks.Count == 0)) throw new ValidationException("spendBreaks", "A spend tier needs at least one break.");

        return new PromotionDefinition(id)
        {
            Name = name.Trim(),
            Code = code?.Trim().ToUpperInvariant(),
            Kind = kind,
            ScopeKind = scope.Kind,
            ScopeTargetId = scope.TargetId,
            Priority = priority,
            Exclusive = exclusive,
            Percent = percent,
            FixedMinor = fixedMinor,
            MaxDiscountMinor = maxDiscountMinor,
            MinSubtotalMinor = minSubtotalMinor,
            VolumeBreaksJson = JsonSerializer.Serialize(volumeBreaks ?? []),
            SpendBreaksJson = JsonSerializer.Serialize(spendBreaks ?? []),
            StartsAtUtc = startsAtUtc,
            EndsAtUtc = endsAtUtc,
            GlobalLimit = globalLimit,
            PerUserLimit = perUserLimit
        };
    }

    private PromotionDefinition(Guid id) : base(id) { }

    public void SetActive(bool active) => IsActive = active;

    public bool IsAutomatic => Code is null;

    public Promotion ToPromotion() => new(
        Id, Name, Kind, new PromotionScope(ScopeKind, ScopeTargetId), Priority, Exclusive, Percent, FixedMinor, MaxDiscountMinor,
        MinSubtotalMinor,
        JsonSerializer.Deserialize<List<VolumeBreak>>(VolumeBreaksJson) ?? [],
        JsonSerializer.Deserialize<List<SpendBreak>>(SpendBreaksJson) ?? [],
        StartsAtUtc, EndsAtUtc, GlobalLimit, PerUserLimit);
}

/// <summary>How many orders of one user hold a use of a promotion.</summary>
public sealed class PromotionUserUsage
{
    public Guid PromotionId { get; private set; }
    public Guid UserId { get; private set; }
    public int Uses { get; private set; }

    private PromotionUserUsage() { }
}

/// <summary>Which promotions an order used and what each took off, so a cancellation can give the use back and finance can see it.</summary>
public sealed class PromotionRedemption : Entity<Guid>
{
    public Guid OrderId { get; private set; }
    public Guid PromotionId { get; private set; }
    public Guid UserId { get; private set; }
    public long DiscountMinor { get; private set; }

    private PromotionRedemption() { }

    public PromotionRedemption(Guid orderId, Guid promotionId, Guid userId, long discountMinor) : base(Guid.NewGuid())
    {
        OrderId = orderId;
        PromotionId = promotionId;
        UserId = userId;
        DiscountMinor = discountMinor;
    }
}
