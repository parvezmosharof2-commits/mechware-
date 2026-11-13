using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Mechware.Application.Companies;
using Mechware.Application.Orders;
using Mechware.Application.Shipping;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Shipping;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Shipping;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>Shared set-up: a district of its own per test (so tests never share a zone), addresses in it, and the service.</summary>
public static class ShippingTestKit
{
    /// <summary>
    /// What Development/Testing hosts use: free while no zone exists at all. The older order tests have no zones; their test
    /// databases are their own, so this never hides a zone. Shipping itself is tested with the production options.
    /// </summary>
    public static IOrderShippingQuoter DevelopmentQuoter(MechwareDbContext db) =>
        new OrderShippingQuoter(new ShippingRateProvider(db), new ShippingOptions { AllowFreeWhenNoZones = true });

    /// <summary>Test variants weigh 0.5 kg. Up to 1 kg: 60.00; up to 5 kg: 120.00; heavier: quote only.</summary>
    public static ShippingRateCard Card(long? freeFrom = null, int? heavyUnitGrams = null) =>
        new([new WeightBand(1_000, 6_000), new WeightBand(5_000, 12_000)], freeFrom, heavyUnitGrams, null, 50_000);

    public static async Task<(Guid Id, string NameEn, string NameBn)> DistrictAsync(MechwareDbContext db)
    {
        var id = Guid.NewGuid();
        var tag = id.ToString("N")[..8];
        db.BdLocations.Add(new BdLocation(id, null, "district", "District " + tag, "জেলা " + tag, "outside_dhaka"));
        await db.SaveChangesAsync();
        return (id, "District " + tag, "জেলা " + tag);
    }

    public static async Task<(Guid Id, string NameEn, string NameBn)> ZoneAsync(MechwareDbContext db, ShippingRateCard card)
    {
        var district = await DistrictAsync(db);
        db.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), "z" + Guid.NewGuid().ToString("N")[..10], "Zone", "জোন", [district.Id], card));
        await db.SaveChangesAsync();
        return district;
    }

    public static Address AddressIn(Guid user, string district, bool isDefault = false) => new(
        Guid.NewGuid(), user, "Plant", "Engr. Receiver", "+8801711000000", "Road 1, Plot 2", null, null, "Sadar", district, "Dhaka", "1000",
        isDefault, false);
}

/// <summary>
/// E8.2: shipping is priced from the zones and the variants' weights, inside the order's own pricing, exactly once, and refused
/// with a stable code when it cannot be priced. The service runs with the production options (fail closed).
/// </summary>
public class OrderShippingTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<T> With<T>(Func<OrderService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        var quoter = new OrderShippingQuoter(new ShippingRateProvider(db), new ShippingOptions());
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System, quoter));
    }

    private async Task<SeededVariant> SeedAsync(long price = 100_000)
    {
        await using var db = fixture.CreateContext();
        return await CatalogTestData.SeedVariantAsync(db, 50, priceMinor: price);
    }

    private async Task<(Guid Id, string NameEn, string NameBn)> ZoneAsync(ShippingRateCard card)
    {
        await using var db = fixture.CreateContext();
        return await ShippingTestKit.ZoneAsync(db, card);
    }

    private async Task<Guid> AddressAsync(Guid user, string district)
    {
        await using var db = fixture.CreateContext();
        var address = ShippingTestKit.AddressIn(user, district);
        db.Addresses.Add(address);
        await db.SaveChangesAsync();
        return address.Id;
    }

    private async Task<int> ReservedAsync(Guid variant)
    {
        await using var db = fixture.CreateContext();
        return await db.InventoryItems.Where(i => i.VariantId == variant).SumAsync(i => i.Reserved);
    }

    private async Task<Domain.Orders.Order> OrderAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.Orders.AsNoTracking().Include(o => o.SubOrders).ThenInclude(s => s.Items).SingleAsync(o => o.Id == id);
    }

    private static PlaceOrderRequest Req(Guid user, Guid address, params (Guid Variant, int Qty)[] lines) =>
        new(user, Key(), lines.Select(l => new PlaceOrderLine(l.Variant, l.Qty)).ToList(), ShippingAddressId: address);

    [PostgresFact]
    public async Task Shipping_ComesFromTheZone_AndIsInTheGrandTotalExactlyOnce()
    {
        var district = await ZoneAsync(ShippingTestKit.Card());
        var buyer = await TestUsers.CreateAsync(fixture);
        var address = await AddressAsync(buyer, district.NameEn);
        var v = await SeedAsync(price: 100_000);

        var placed = await With(s => s.PlaceAsync(Req(buyer, address, (v.VariantId, 2))));   // 2 x 0.5 kg = exactly 1 kg: first band

        placed.GrandTotalMinor.Should().Be(200_000 + 6_000);
        var order = await OrderAsync(placed.OrderId);
        var sub = order.SubOrders.Single();
        (order.ShippingMinor, sub.ShippingMinor).Should().Be((6_000, 6_000));
        sub.Items.Sum(i => i.TotalMinor).Should().Be(200_000, "the goods lines carry no shipping");
        sub.TotalMinor.Should().Be(sub.Items.Sum(i => i.TotalMinor) + sub.ShippingMinor);
        order.GrandTotalMinor.Should().Be(order.SubOrders.Sum(s => s.TotalMinor));
        JsonDocument.Parse(order.ShipToJson).RootElement.GetProperty("districtId").GetGuid().Should().Be(district.Id);
    }

    [PostgresFact]
    public async Task TwoSellers_EachPayForTheirOwnParcel_AndTheOrderSumsThem()
    {
        var district = await ZoneAsync(ShippingTestKit.Card());
        var buyer = await TestUsers.CreateAsync(fixture);
        var address = await AddressAsync(buyer, district.NameEn);
        var light = await SeedAsync(price: 100_000);
        var heavy = await SeedAsync(price: 50_000);

        var placed = await With(s => s.PlaceAsync(Req(buyer, address, (light.VariantId, 1), (heavy.VariantId, 6))));   // 0.5 kg and 3 kg

        var order = await OrderAsync(placed.OrderId);
        order.SubOrders.Single(s => s.Items.Single().VariantId == light.VariantId).ShippingMinor.Should().Be(6_000);
        order.SubOrders.Single(s => s.Items.Single().VariantId == heavy.VariantId).ShippingMinor.Should().Be(12_000);
        order.ShippingMinor.Should().Be(18_000);
        order.GrandTotalMinor.Should().Be(100_000 + 300_000 + 18_000);
    }

    [PostgresFact]
    public async Task FreeShipping_StartsExactlyAtTheThreshold_OnTheGoodsTotal()
    {
        var atThreshold = await ZoneAsync(ShippingTestKit.Card(freeFrom: 100_000));
        var onePoishaShort = await ZoneAsync(ShippingTestKit.Card(freeFrom: 100_001));
        var buyer = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync(price: 100_000);   // goods total 1,000.00 (VAT inside)
        var freeAddress = await AddressAsync(buyer, atThreshold.NameEn);
        var paidAddress = await AddressAsync(buyer, onePoishaShort.NameEn);

        var free = await With(s => s.PlaceAsync(Req(buyer, freeAddress, (v.VariantId, 1))));
        var paid = await With(s => s.PlaceAsync(Req(buyer, paidAddress, (v.VariantId, 1))));

        (free.GrandTotalMinor, (await OrderAsync(free.OrderId)).ShippingMinor).Should().Be((100_000, 0));
        (paid.GrandTotalMinor, (await OrderAsync(paid.OrderId)).ShippingMinor).Should().Be((106_000, 6_000));
    }

    [PostgresFact]
    public async Task ADestinationNoZoneServes_IsRefused_AndHoldsNoStock()
    {
        await ZoneAsync(ShippingTestKit.Card());   // zones exist, just not for these districts
        string unserved;
        await using (var db = fixture.CreateContext()) unserved = (await ShippingTestKit.DistrictAsync(db)).NameEn;
        var buyer = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();

        foreach (var district in new[] { unserved, "Atlantis" })   // a known district with no zone; a name that is no district
        {
            var address = await AddressAsync(buyer, district);
            var act = () => With(s => s.PlaceAsync(Req(buyer, address, (v.VariantId, 1))));
            (await act.Should().ThrowAsync<UnprocessableException>()).Which.Code.Should().Be(IOrderShippingQuoter.NotAvailable);
        }
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task QuoteOnlyGoods_AreRefused_WithTheReasonAndSku_AndHoldNoStock()
    {
        var heavyRule = await ZoneAsync(ShippingTestKit.Card(heavyUnitGrams: 400));   // one 0.5 kg unit is "heavy" here
        var normal = await ZoneAsync(ShippingTestKit.Card());
        var buyer = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();
        string sku;
        await using (var db = fixture.CreateContext()) sku = await db.ProductVariants.Where(x => x.Id == v.VariantId).Select(x => x.Sku).SingleAsync();
        var heavyAddress = await AddressAsync(buyer, heavyRule.NameEn);
        var normalAddress = await AddressAsync(buyer, normal.NameEn);

        var heavy = () => With(s => s.PlaceAsync(Req(buyer, heavyAddress, (v.VariantId, 1))));
        var tooMuch = () => With(s => s.PlaceAsync(Req(buyer, normalAddress, (v.VariantId, 11))));   // 5.5 kg

        var e1 = (await heavy.Should().ThrowAsync<UnprocessableException>()).Which;
        e1.Code.Should().Be(IOrderShippingQuoter.QuoteOnly);
        e1.Details.Should().ContainEquivalentOf(new ValidationError(ShippingCalculator.HeavyUnit, sku));
        var e2 = (await tooMuch.Should().ThrowAsync<UnprocessableException>()).Which;
        e2.Code.Should().Be(IOrderShippingQuoter.QuoteOnly);
        e2.Details.Should().ContainSingle().Which.Field.Should().Be(ShippingCalculator.AboveLastBand);
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task AVariantWithoutWeight_IsRefusedByItsSku_NeverPricedAsFree()
    {
        var district = await ZoneAsync(ShippingTestKit.Card(freeFrom: 1));   // even with free shipping, a missing weight is an error
        var buyer = await TestUsers.CreateAsync(fixture);
        var address = await AddressAsync(buyer, district.NameEn);
        var v = await SeedAsync();
        string sku;
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE product_variants SET weight_kg = 0 WHERE id = {v.VariantId}");
            sku = await db.ProductVariants.Where(x => x.Id == v.VariantId).Select(x => x.Sku).SingleAsync();
        }

        var act = () => With(s => s.PlaceAsync(Req(buyer, address, (v.VariantId, 1))));

        var e = (await act.Should().ThrowAsync<UnprocessableException>()).Which;
        e.Code.Should().Be(IOrderShippingQuoter.WeightMissing);
        e.Message.Should().Contain(sku);
        e.Details.Should().ContainSingle().Which.Should().Be(new ValidationError("sku", sku));
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task ThePreview_ChargesWhatPlacementCharges_AndHoldsNothing()
    {
        var district = await ZoneAsync(ShippingTestKit.Card());
        var buyer = await TestUsers.CreateAsync(fixture);
        var address = await AddressAsync(buyer, district.NameBn);   // the Bangla district name resolves too
        var a = await SeedAsync(100_000);
        var b = await SeedAsync(50_000);
        var lines = new List<PlaceOrderLine> { new(a.VariantId, 1), new(b.VariantId, 6) };

        var quote = await With(s => s.QuoteShippingAsync(new ShippingQuoteRequest(buyer, lines, address)));

        (quote.TotalMinor, quote.FreeShippingApplied).Should().Be((18_000, false));
        quote.PerSeller.Sum(p => p.ShippingMinor).Should().Be(quote.TotalMinor);
        (await ReservedAsync(a.VariantId) + await ReservedAsync(b.VariantId)).Should().Be(0);
        await using (var db = fixture.CreateContext()) (await db.Orders.CountAsync(o => o.UserId == buyer)).Should().Be(0);

        var placed = await With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), lines, address)));
        var order = await OrderAsync(placed.OrderId);
        order.SubOrders.Select(s => (s.SellerId, s.ShippingMinor)).Should().BeEquivalentTo(quote.PerSeller.Select(p => (p.SellerId, p.ShippingMinor)));
    }

    [PostgresFact]
    public async Task ACompanyOrder_ShipsToItsSitesDistrict_AndKeepsItsPurchaseOrderNumber()
    {
        var district = await ZoneAsync(ShippingTestKit.Card());
        var (buyer, company) = await CompanyBuyerAsync(district.NameEn);
        var v = await SeedAsync();

        var placed = await With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], CompanyId: company,
            PurchaseOrderNumber: "  PO/2026-001 #7_a.b  ")));

        var dto = await With(s => s.GetAsync(buyer, placed.OrderId));
        dto.PurchaseOrderNumber.Should().Be("PO/2026-001 #7_a.b");
        dto.ShippingMinor.Should().Be(6_000);
        dto.GrandTotalMinor.Should().Be(dto.SubOrders.Single().Items.Sum(i => i.TotalMinor) + 6_000);
    }

    [PostgresTheory]
    [InlineData("PO-0000000001-0000000002-0000000003-00004")]   // 41 characters
    [InlineData("PO\n2026")]
    [InlineData("PO\u00002026")]
    [InlineData("=HYPERLINK(\"x\")")]
    [InlineData("পিও-১২")]
    public async Task ABadPurchaseOrderNumber_IsRefused_BeforeAnyStockIsHeld(string po)
    {
        var district = await ZoneAsync(ShippingTestKit.Card());
        var (buyer, company) = await CompanyBuyerAsync(district.NameEn);
        var v = await SeedAsync();

        var act = () => With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], CompanyId: company, PurchaseOrderNumber: po)));

        (await act.Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainSingle(e => e.Field == "purchaseOrderNumber");
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task APurchaseOrderNumber_OnAPersonalOrder_IsRefused()
    {
        var district = await ZoneAsync(ShippingTestKit.Card());
        var buyer = await TestUsers.CreateAsync(fixture);
        var address = await AddressAsync(buyer, district.NameEn);
        var v = await SeedAsync();

        var act = () => With(s => s.PlaceAsync(Req(buyer, address, (v.VariantId, 1)) with { PurchaseOrderNumber = "PO-1" }));

        (await act.Should().ThrowAsync<ValidationException>()).Which.Errors.Should().ContainSingle(e => e.Field == "purchaseOrderNumber");
    }

    [PostgresFact]
    public async Task TheSameKey_WithAnotherPurchaseOrderNumber_IsRefused_AndWithTheSameOneReplays()
    {
        var district = await ZoneAsync(ShippingTestKit.Card());
        var (buyer, company) = await CompanyBuyerAsync(district.NameEn);
        var v = await SeedAsync();
        var key = Key();
        PlaceOrderRequest Po(string? po) => new(buyer, key, [new PlaceOrderLine(v.VariantId, 1)], CompanyId: company, PurchaseOrderNumber: po);

        var first = await With(s => s.PlaceAsync(Po("PO-1")));
        var replay = await With(s => s.PlaceAsync(Po(" PO-1 ")));   // same number once trimmed
        var other = () => With(s => s.PlaceAsync(Po("PO-2")));
        var none = () => With(s => s.PlaceAsync(Po(null)));

        (replay.OrderId, replay.Replayed).Should().Be((first.OrderId, true));
        (await other.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await none.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("IDEMPOTENCY_KEY_REUSED");
        (await ReservedAsync(v.VariantId)).Should().Be(1);
    }

    private async Task<(Guid Buyer, Guid Company)> CompanyBuyerAsync(string district)
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var buyer = await TestUsers.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        var svc = new CompanyService(db);
        var bin = (9_000_000_000_000L + Random.Shared.NextInt64(0, 999_999_999_999L)).ToString("D13");
        var tag = Guid.NewGuid().ToString("N")[..8];
        var company = (await svc.RegisterAsync(admin, new RegisterCompanyRequest("Ship Ltd " + tag, "TL-SH-" + tag, bin, null, null))).Id;
        await svc.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await svc.AddSiteAsync(admin, company, new SiteRequest("Main plant", "Store Keeper", "+8801911000000", "Plot 7, BSCIC",
            null, "Sadar", district, "Dhaka", "1750", IsDefaultDelivery: true));
        await svc.AdminActivateAsync(company);
        return (buyer, company);
    }
}

/// <summary>Before any zone exists: Development/Testing may ship for 0; production refuses. Each class has its own empty database.</summary>
public class OrderShippingWithoutZonesTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private async Task<T> With<T>(bool allowFreeWhenNoZones, Func<OrderService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        var quoter = new OrderShippingQuoter(new ShippingRateProvider(db), new ShippingOptions { AllowFreeWhenNoZones = allowFreeWhenNoZones });
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System, quoter));
    }

    [PostgresFact]
    public async Task WithTheDevelopmentSwitch_ShippingIsZero_AndWithoutIt_TheOrderIsRefused()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        SeededVariant v;
        await using (var db = fixture.CreateContext()) v = await CatalogTestData.SeedVariantAsync(db, 10, priceMinor: 100_000);
        PlaceOrderRequest Req() => new(buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(v.VariantId, 1)]);

        var closed = () => With(false, s => s.PlaceAsync(Req()));
        (await closed.Should().ThrowAsync<UnprocessableException>()).Which.Code.Should().Be(IOrderShippingQuoter.NotAvailable);

        var placed = await With(true, s => s.PlaceAsync(Req()));
        placed.GrandTotalMinor.Should().Be(100_000);
        await using var check = fixture.CreateContext();
        (await check.InventoryItems.Where(i => i.VariantId == v.VariantId).SumAsync(i => i.Reserved)).Should().Be(1, "only the allowed order holds stock");
    }
}

/// <summary>The switch through the real host: off by default outside Development/Testing, and refused there if switched on.</summary>
public class OrderShippingStartupTests
{
    private static Dictionary<string, string> ProductionSettings(string? allowFree = null)
    {
        var settings = new Dictionary<string, string>
        {
            ["Jwt:Secret"] = "prod-like-secret-" + Guid.NewGuid().ToString("N"),
            ["Storage:AccessKey"] = "test-access-key",
            ["Storage:SecretKey"] = "test-secret-key",
            // Field encryption is process-wide: keep the previous test key so parallel tests still read their data.
            ["Encryption:FieldKey"] = TestEncryption.KeyBase64,
            ["Encryption:PreviousFieldKeys"] = TestEncryption.PreviousKeyBase64,
            ["Documents:LocalPath"] = TestEncryption.DocumentsPath,
            ["Antivirus:Host"] = "127.0.0.1",
            ["Email:Host"] = "localhost",
            ["Email:FromAddress"] = "no-reply@mechware.test",
            ["Storefront:BaseUrl"] = "https://shop.mechware.test",
            ["Search:ApiKey"] = "test-search-key",
        };
        if (allowFree is not null) settings["Shipping:AllowFreeWhenNoZones"] = allowFree;
        return settings;
    }

    [PostgresFact]
    public async Task Production_RefusesToStart_WithFreeShippingWithoutZonesSwitchedOn()
    {
        await using var factory = new DbBackedFactory("Production", ProductionSettings(allowFree: "true"));
        FluentActions.Invoking(() => factory.CreateClient()).Should().Throw<Exception>()
            .Where(e => e.ToString().Contains("Shipping:AllowFreeWhenNoZones is only allowed in Development or Testing"));
    }

    [PostgresTheory]
    [InlineData("Production", false)]
    [InlineData("Development", true)]
    public async Task TheContainersOrderService_FailsClosedInProduction_AndShipsFreeInDevelopment_WhenNoZoneExists(string environment, bool free)
    {
        await using var factory = new DbBackedFactory(environment, environment == "Production" ? ProductionSettings() : null);
        using var client = factory.CreateClient();
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<MechwareDbContext>();
        (await db.ShippingZones.AnyAsync()).Should().BeFalse();
        var user = User.Create($"buyer.{Guid.NewGuid():N}@example.test", null, null, "Buyer");
        user.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(user);
        db.Addresses.Add(TestUsers.DefaultAddress(user.Id));
        await db.SaveChangesAsync();
        var v = await CatalogTestData.SeedVariantAsync(db, 5, priceMinor: 100_000);

        var orders = scope.ServiceProvider.GetRequiredService<IOrderService>();
        var quote = () => orders.QuoteShippingAsync(new ShippingQuoteRequest(user.Id, [new PlaceOrderLine(v.VariantId, 1)]));

        if (free) (await quote()).TotalMinor.Should().Be(0);
        else (await quote.Should().ThrowAsync<UnprocessableException>()).Which.Code.Should().Be(IOrderShippingQuoter.NotAvailable);
    }
}
