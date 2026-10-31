using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Identity;
using ShopVerse.Domain.Orders;
using ShopVerse.Domain.Shipping;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Orders;

/// <summary>
/// The tariff table is global, so these tests share one class fixture (one database) and run one after another inside it.
/// Each test sets exactly the tariff it needs and removes it afterwards, so other test classes never see a tariff.
/// </summary>
public class OrderShippingTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    public Task InitializeAsync() => ClearRatesAsync();
    public Task DisposeAsync() => ClearRatesAsync();

    private async Task ClearRatesAsync()
    {
        if (!TestInfra.PostgresAvailable) return;
        await using var db = fixture.CreateContext();
        await db.Database.ExecuteSqlRawAsync("DELETE FROM shipping_rates");
    }

    private async Task<T> With<T>(Func<OrderService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System));
    }

    private async Task SetRatesAsync(params ShippingRate[] rates)
    {
        await using var db = fixture.CreateContext();
        db.ShippingRates.AddRange(rates);
        await db.SaveChangesAsync();
    }

    private static ShippingRate Rate(ShippingZone zone, long baseMinor, long perKg, int? maxGrams = null, int? maxMm = null) =>
        new(Guid.NewGuid(), zone, baseMinor, perKg, maxParcelGrams: maxGrams, maxDimensionMm: maxMm);

    private async Task<SeededVariant> SeedAsync(decimal weightKg = 2m)
    {
        await using var db = fixture.CreateContext();
        var v = await CatalogTestData.SeedVariantAsync(db, onHand: 50, priceMinor: 100_000);
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET weight_kg = {weightKg} WHERE id = {v.VariantId}");
        return v;
    }

    private async Task<Guid> AddressAsync(Guid user, string upazila, string district, string division)
    {
        await using var db = fixture.CreateContext();
        var a = new Address(Guid.NewGuid(), user, "Plant", "Engr. Test", "+8801711000004", "Plot 1", null, null, upazila, district, division, "1200", false, false, null, null);
        db.Addresses.Add(a);
        await db.SaveChangesAsync();
        return a.Id;
    }

    private static PlaceOrderRequest Req(Guid user, Guid variant, int qty, Guid? address) =>
        new(user, Key(), [new PlaceOrderLine(variant, qty)], null, null, false, null, address);

    [PostgresFact]
    public async Task WithNoTariffConfigured_ShippingIsZero_AsBefore()
    {
        var user = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();
        var placed = await With(s => s.PlaceAsync(Req(user, v.VariantId, 1, null)));
        placed.GrandTotalMinor.Should().Be(100_000);
    }

    [PostgresFact]
    public async Task TheFeeIsBasePlusPerKilo_ByTheZoneOfTheSavedAddress()
    {
        await SetRatesAsync(Rate(ShippingZone.InsideDhaka, 6_000, 2_000), Rate(ShippingZone.DhakaSuburbs, 9_000, 3_000), Rate(ShippingZone.OutsideDhaka, 15_000, 5_000));
        var user = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync(weightKg: 2m);   // 2 kg per unit; the default 10 cm cube adds only 0.2 kg of volume

        var gulshan = await AddressAsync(user, "Gulshan", "Dhaka", "Dhaka");
        var tongi = await AddressAsync(user, "Tongi Industrial Zone", "Gazipur", "Dhaka");
        var halishahar = await AddressAsync(user, "Chattogram EPZ (Halishahar)", "Chattogram", "Chattogram");

        var inside = await With(s => s.PlaceAsync(Req(user, v.VariantId, 1, gulshan)));
        var suburb = await With(s => s.PlaceAsync(Req(user, v.VariantId, 1, tongi)));
        var outside = await With(s => s.PlaceAsync(Req(user, v.VariantId, 1, halishahar)));

        inside.GrandTotalMinor.Should().Be(100_000 + 6_000 + 2 * 2_000);
        suburb.GrandTotalMinor.Should().Be(100_000 + 9_000 + 2 * 3_000);
        outside.GrandTotalMinor.Should().Be(100_000 + 15_000 + 2 * 5_000);
    }

    [PostgresFact]
    public async Task EachSellerShipsSeparately_SoEachSubOrderCarriesItsOwnFee()
    {
        await SetRatesAsync(Rate(ShippingZone.InsideDhaka, 6_000, 2_000));
        var user = await TestUsers.CreateAsync(fixture);
        var a = await SeedAsync(weightKg: 1m);
        var b = await SeedAsync(weightKg: 3m);
        var address = await AddressAsync(user, "Gulshan", "Dhaka", "Dhaka");

        var placed = await With(s => s.PlaceAsync(new PlaceOrderRequest(user, Key(), [new PlaceOrderLine(a.VariantId, 1), new PlaceOrderLine(b.VariantId, 1)], null, null, false, null, address)));

        await using var db = fixture.CreateContext();
        var subs = await db.SubOrders.AsNoTracking().Where(s => s.OrderId == placed.OrderId).ToListAsync();
        subs.Select(s => s.ShippingMinor).OrderBy(x => x).Should().Equal(6_000 + 1 * 2_000, 6_000 + 3 * 2_000);   // 1 kg and 3 kg billed (the 10 cm box is only 0.2 kg by volume)
        (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == placed.OrderId)).ShippingMinor.Should().Be(subs.Sum(s => s.ShippingMinor));
    }

    [PostgresFact]
    public async Task TheZoneComesFromTheServersAddressRecord_NotFromAnythingTheClientTypes()
    {
        await SetRatesAsync(Rate(ShippingZone.InsideDhaka, 1_000, 0), Rate(ShippingZone.OutsideDhaka, 50_000, 0));
        var user = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();
        var chattogram = await AddressAsync(user, "Chattogram EPZ (Halishahar)", "Chattogram", "Chattogram");

        // The client also sends a free-text destination claiming to be in Dhaka. It has no effect on the price.
        var request = new PlaceOrderRequest(user, Key(), [new PlaceOrderLine(v.VariantId, 1)], """{ "district": "Dhaka", "upazila": "Gulshan" }""", null, false, null, chattogram);
        var placed = await With(s => s.PlaceAsync(request));

        placed.GrandTotalMinor.Should().Be(100_000 + 50_000, "Chattogram is outside Dhaka whatever the client wrote");
        await using var db = fixture.CreateContext();
        var snapshot = (await db.Orders.AsNoTracking().SingleAsync(o => o.Id == placed.OrderId)).ShipToJson;
        snapshot.Should().Contain("Chattogram").And.NotContain("Gulshan", "the stored destination is built from the saved address");
    }

    [PostgresFact]
    public async Task AnAddressThatIsNotTheBuyers_IsRefused()
    {
        await SetRatesAsync(Rate(ShippingZone.InsideDhaka, 1_000, 0));
        var owner = await TestUsers.CreateAsync(fixture);
        var thief = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();
        var ownersAddress = await AddressAsync(owner, "Gulshan", "Dhaka", "Dhaka");

        var act = () => With(s => s.PlaceAsync(Req(thief, v.VariantId, 1, ownersAddress)));
        await act.Should().ThrowAsync<ValidationException>();
    }

    [PostgresFact]
    public async Task OnceATariffExists_AnAddressIsRequired_AndAnUnservedZoneIsRefused()
    {
        await SetRatesAsync(Rate(ShippingZone.InsideDhaka, 1_000, 0));   // no tariff for the other zones
        var user = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();

        await Assert.ThrowsAsync<ValidationException>(() => With(s => s.PlaceAsync(Req(user, v.VariantId, 1, null))));
        var far = await AddressAsync(user, "Chattogram EPZ (Halishahar)", "Chattogram", "Chattogram");
        (await Assert.ThrowsAsync<ConflictException>(() => With(s => s.PlaceAsync(Req(user, v.VariantId, 1, far))))).Code.Should().Be("SHIPPING_NOT_AVAILABLE");

        await using var db = fixture.CreateContext();
        (await db.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == v.VariantId)).Reserved.Should().Be(0, "refused orders hold no stock");
    }

    [PostgresFact]
    public async Task AnUnknownPlace_IsTreatedAsTheDearestZone_NeverTheCheapest()
    {
        await SetRatesAsync(Rate(ShippingZone.InsideDhaka, 1_000, 0), Rate(ShippingZone.OutsideDhaka, 40_000, 0));
        var user = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();
        var nowhere = await AddressAsync(user, "Atlantis", "Nowhere", "Nowhere");

        var placed = await With(s => s.PlaceAsync(Req(user, v.VariantId, 1, nowhere)));

        placed.GrandTotalMinor.Should().Be(100_000 + 40_000);
    }

    [PostgresFact]
    public async Task AParcelOverTheLimit_NeedsAQuote_AndIsRefusedWithoutHoldingStock()
    {
        await SetRatesAsync(Rate(ShippingZone.InsideDhaka, 1_000, 100, maxGrams: 10_000));
        var user = await TestUsers.CreateAsync(fixture);
        var heavy = await SeedAsync(weightKg: 11m);
        var address = await AddressAsync(user, "Gulshan", "Dhaka", "Dhaka");

        var act = () => With(s => s.PlaceAsync(Req(user, heavy.VariantId, 1, address)));

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("SHIPPING_QUOTE_REQUIRED");
        await using var db = fixture.CreateContext();
        (await db.InventoryItems.AsNoTracking().SingleAsync(i => i.VariantId == heavy.VariantId)).Reserved.Should().Be(0);
    }

    [PostgresFact]
    public async Task ShippingIsNotDiscountedByPromotionsOrTaxed_ItIsAddedAsIs()
    {
        await SetRatesAsync(Rate(ShippingZone.InsideDhaka, 7_777, 0));
        var user = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();
        var address = await AddressAsync(user, "Gulshan", "Dhaka", "Dhaka");

        var placed = await With(s => s.PlaceAsync(Req(user, v.VariantId, 2, address)));

        placed.GrandTotalMinor.Should().Be(200_000 + 7_777);
        await using var db = fixture.CreateContext();
        var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == placed.OrderId);
        (order.ShippingMinor, order.DiscountMinor).Should().Be((7_777, 0));
    }
}
