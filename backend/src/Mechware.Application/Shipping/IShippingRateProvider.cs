using Mechware.Domain.Shipping;

namespace Mechware.Application.Shipping;

/// <summary>Read-only source of the active shipping zones and tariffs. The pricing seam (E8.2) feeds these to <see cref="ShippingCalculator"/>.</summary>
public interface IShippingRateProvider
{
    Task<IReadOnlyList<ShippingZoneConfig>> GetActiveZonesAsync(CancellationToken ct);
}
