using Microsoft.Extensions.Configuration;

namespace Mechware.Infrastructure.Pricing;

/// <summary>Price-list settings (ADR-0008 §1, §4). The hard limits are domain constants (<c>PriceListLimits</c>), not options.</summary>
public sealed class PricingOptions
{
    public const string SectionName = "Pricing";

    /// <summary>
    /// Passed into <c>PriceList.Submit</c> and <c>Approve</c>: with false (the default everywhere) a price below cost is refused.
    /// Only Development and Testing may turn it on; anywhere else the host refuses to start, so production fails closed.
    /// </summary>
    public bool AllowBelowCost { get; init; }

    /// <summary>Reads <c>Pricing:AllowBelowCost</c>; unset means false in every environment.</summary>
    public static PricingOptions From(IConfiguration configuration, bool developmentOrTesting)
    {
        var raw = configuration[$"{SectionName}:AllowBelowCost"];
        bool allow = false;
        if (!string.IsNullOrWhiteSpace(raw) && !bool.TryParse(raw, out allow))
            throw new InvalidOperationException("Pricing:AllowBelowCost must be true or false.");
        if (allow && !developmentOrTesting)
            throw new InvalidOperationException("Pricing:AllowBelowCost is only allowed in Development or Testing: production never sells below cost.");
        return new PricingOptions { AllowBelowCost = allow };
    }
}
