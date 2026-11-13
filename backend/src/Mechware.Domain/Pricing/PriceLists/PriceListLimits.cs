namespace Mechware.Domain.Pricing.PriceLists;

/// <summary>
/// Hard limits of price lists (ADR-0008 §1, owner decision 5). They are domain constants, not options: the resolver
/// derives a list's start from <see cref="ActivationDelay"/>, so changing it would move the start of lists that were
/// approved long ago and break "resolving again at the order's instant gives the same price" (§10).
/// </summary>
public static class PriceListLimits
{
    /// <summary>A list starts no earlier than this long after its approval (§10).</summary>
    public static readonly TimeSpan ActivationDelay = TimeSpan.FromSeconds(60);

    /// <summary>৳1,000,000,000.00. No entry above it is ever used, and no catalogue price above it is priced.</summary>
    public const long MaxUnitPriceMinor = 100_000_000_000L;

    public const int MaxValidityDays = 366;
    public const int MaxEntriesPerList = 5_000;
}
