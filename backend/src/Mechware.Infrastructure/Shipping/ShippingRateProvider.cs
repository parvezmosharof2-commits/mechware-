using Microsoft.EntityFrameworkCore;
using Mechware.Application.Shipping;
using Mechware.Domain.Shipping;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Shipping;

internal sealed class ShippingRateProvider(MechwareDbContext db) : IShippingRateProvider
{
    public async Task<IReadOnlyList<ShippingZoneConfig>> GetActiveZonesAsync(CancellationToken ct)
    {
        var zones = await db.ShippingZones.AsNoTracking().AsSplitQuery()
            .Include(z => z.Districts).Include(z => z.Bands)
            .Where(z => z.IsActive).OrderBy(z => z.Code).ToListAsync(ct);
        return zones.Select(z => z.ToConfig()).ToList();
    }
}
