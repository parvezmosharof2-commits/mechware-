using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.Domain.Shipping;
using Mechware.Infrastructure.Shipping;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Shipping;

public sealed class ShippingRateProviderTests(PostgresDatabaseFixture db) : IClassFixture<PostgresDatabaseFixture>
{
    private static ShippingRateCard Card() => new([new WeightBand(1_000, 100), new WeightBand(5_000, 250)], 50_000, 10_000, 1_000, 900);

    private static async Task<Guid[]> AddDistrictsAsync(Mechware.Infrastructure.Persistence.MechwareDbContext ctx, int n)
    {
        var ids = Enumerable.Range(0, n).Select(_ => Guid.NewGuid()).ToArray();
        foreach (var id in ids) ctx.BdLocations.Add(new BdLocation(id, null, "district", "D" + id.ToString("N")[..6], "জেলা", "outside_dhaka"));
        await ctx.SaveChangesAsync();
        return ids;
    }

    [PostgresFact]
    public async Task Provider_reads_active_zones_with_districts_and_bands_and_feeds_the_calculator()
    {
        await using var ctx = db.CreateContext();
        var d = await AddDistrictsAsync(ctx, 3);
        var code = "t" + Guid.NewGuid().ToString("N")[..8];
        ctx.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), code, "Zone A", "জোন এ", [d[0], d[1]], Card()));
        ctx.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), code + "x", "Zone off", "জোন", [d[2]], Card(), isActive: false));
        await ctx.SaveChangesAsync();

        await using var read = db.CreateContext();
        var zones = await new ShippingRateProvider(read).GetActiveZonesAsync(CancellationToken.None);
        var zone = zones.Single(z => z.Code == code);
        zone.DistrictIds.Should().BeEquivalentTo([d[0], d[1]]);
        zone.Rates.Bands.Should().Equal(Card().Bands);
        zone.Rates.FreeShippingThresholdMinor.Should().Be(50_000);
        zone.Rates.HeavyUnitGrams.Should().Be(10_000);
        zone.Rates.OversizeDimensionMm.Should().Be(1_000);
        zone.Rates.MaxFeeMinor.Should().Be(900);
        zones.Should().NotContain(z => z.Code == code + "x"); // inactive zone is not served
        read.ChangeTracker.Entries().Should().BeEmpty("the provider reads without tracking");

        var seller = Guid.NewGuid();
        ShippingCalculator.Calculate(zones, d[1], [new ShippingLine(Guid.NewGuid(), seller, 1, 1_001)], 1)
            .Should().BeOfType<ShippingQuote.Priced>().Which.TotalMinor.Should().Be(250);
        ShippingCalculator.Calculate(zones, d[2], [new ShippingLine(Guid.NewGuid(), seller, 1, 500)], 1)
            .Should().BeOfType<ShippingQuote.NoZone>();
    }

    [PostgresFact]
    public async Task A_district_in_two_zones_is_refused_by_the_database()
    {
        await using var ctx = db.CreateContext();
        var d = await AddDistrictsAsync(ctx, 1);
        ctx.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), "u" + Guid.NewGuid().ToString("N")[..8], "One", "এক", [d[0]], Card()));
        await ctx.SaveChangesAsync();

        await using var ctx2 = db.CreateContext();
        ctx2.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), "u" + Guid.NewGuid().ToString("N")[..8], "Two", "দুই", [d[0]], Card()));
        var act = () => ctx2.SaveChangesAsync();
        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [PostgresFact]
    public async Task A_zone_code_is_unique_and_a_district_must_exist()
    {
        await using var ctx = db.CreateContext();
        var d = await AddDistrictsAsync(ctx, 2);
        var code = "c" + Guid.NewGuid().ToString("N")[..8];
        ctx.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), code, "One", "এক", [d[0]], Card()));
        await ctx.SaveChangesAsync();

        await using var dup = db.CreateContext();
        dup.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), code, "Dup", "ডুপ", [d[1]], Card()));
        await ((Func<Task>)(() => dup.SaveChangesAsync())).Should().ThrowAsync<DbUpdateException>();

        await using var ghost = db.CreateContext();
        ghost.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), "g" + Guid.NewGuid().ToString("N")[..8], "Ghost", "ভূত", [Guid.NewGuid()], Card()));
        await ((Func<Task>)(() => ghost.SaveChangesAsync())).Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public void The_domain_also_refuses_a_district_listed_twice_in_one_zone()
    {
        var d = Guid.NewGuid();
        var act = () => new ShippingZone(Guid.NewGuid(), "dup", "A", "এ", [d, d], Card());
        act.Should().Throw<Mechware.Domain.Common.Exceptions.ValidationException>();
    }
}
