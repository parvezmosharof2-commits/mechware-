using Microsoft.EntityFrameworkCore;
using ShopVerse.Domain.Shipping;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Orders;

/// <summary>
/// Gives an empty rate table the business's starting flat fees: Tk 200 inside Dhaka, Tk 500 around Dhaka, Tk 1,000 outside
/// (amounts in poisha). Per-kg is 0 until the business sets one. It only fills an EMPTY table, so a rate the business has
/// changed in the database is never overwritten by a restart.
/// </summary>
public sealed class ShippingRateSeeder(ShopVerseDbContext db)
{
    public static readonly (ShippingZone Zone, long BaseMinor)[] StartingFees =
    [
        (ShippingZone.InsideDhaka, 200_00),
        (ShippingZone.DhakaSuburbs, 500_00),
        (ShippingZone.OutsideDhaka, 1_000_00),
    ];

    public async Task SeedAsync(CancellationToken cancellationToken = default)
    {
        if (await db.ShippingRates.AnyAsync(cancellationToken)) return;
        foreach (var (zone, baseMinor) in StartingFees)
        {
            db.ShippingRates.Add(new ShippingRate(Guid.NewGuid(), zone, baseMinor, perKgMinor: 0));
        }

        await db.SaveChangesAsync(cancellationToken);
    }
}
