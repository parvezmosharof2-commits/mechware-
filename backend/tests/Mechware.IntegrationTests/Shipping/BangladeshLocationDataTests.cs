using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Application.Shipping;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Shipping;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Shipping;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Shipping;

/// <summary>Runs the same SQL the A9 migration runs, so these tests see the real reference data (EnsureCreated has none).</summary>
internal static class LocationInstall
{
    public static async Task InstallAsync(MechwareDbContext db)
    {
        foreach (var sql in BangladeshLocationSql.Statements()) await db.Database.ExecuteSqlRawAsync(sql);
    }
}

/// <summary>A9 through the API: coverage, zone creation and the public location list over the 64 real districts.</summary>
public sealed class BangladeshLocationEndpointsTests : IDisposable
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "ShipStaff#2026!";
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private static MechwareDbContext Db(string c) => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(c).Options);

    private async Task<HttpClient> AdminAsync()
    {
        _factory.CreateClient().Dispose();
        var email = $"loc.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        await using (var db = Db(_factory.ConnectionString))
        {
            await LocationInstall.InstallAsync(db);
            await LocationInstall.InstallAsync(db);   // idempotent: a second run changes nothing
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Location Admin", "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), Permissions.Roles.Admin);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
        }
        var client = _factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return client;
    }

    [PostgresFact]
    public async Task Coverage_ListsAll64DistrictsUncovered_WithDivisions_AndAZoneForDhakaAndGazipurSaves()
    {
        using var admin = await AdminAsync();
        await using (var db = Db(_factory.ConnectionString))
            (await db.BdLocations.CountAsync()).Should().Be(BangladeshLocationSeeder.GetSeedLocations().Count, "the second install added nothing");

        var raw = await (await admin.GetAsync("/api/v1/admin/shipping/coverage")).Content.ReadAsStringAsync();
        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        var uncovered = data.GetProperty("uncovered").EnumerateArray().ToList();
        uncovered.Should().HaveCount(64);
        data.GetProperty("covered").GetArrayLength().Should().Be(0);
        uncovered.Should().OnlyContain(d => !string.IsNullOrEmpty(d.GetProperty("divisionNameEn").GetString()));
        uncovered.Single(d => d.GetProperty("nameEn").GetString() == "Sylhet").GetProperty("divisionNameEn").GetString().Should().Be("Sylhet");
        uncovered.Single(d => d.GetProperty("nameEn").GetString() == "Jamalpur").GetProperty("divisionNameEn").GetString().Should().Be("Mymensingh");

        var body = new
        {
            code = "dhaka-metro",
            nameEn = "Dhaka and Gazipur",
            nameBn = "ঢাকা ও গাজীপুর",
            isActive = true,
            districtIds = new[] { BangladeshLocationSeeder.DistDhakaId, BangladeshLocationSeeder.DistGazipurId },
            rates = new { bands = new[] { new { maxGrams = 1_000, feeMinor = 6_000 } }, freeShippingThresholdMinor = 50_000L, heavyUnitGrams = 8_000, oversizeDimensionMm = 1_000, maxFeeMinor = 50_000L },
        };
        var created = await admin.PostAsJsonAsync("/api/v1/admin/shipping/zones", body);
        created.StatusCode.Should().Be(HttpStatusCode.Created, await created.Content.ReadAsStringAsync());

        var after = JsonDocument.Parse(await (await admin.GetAsync("/api/v1/admin/shipping/coverage")).Content.ReadAsStringAsync()).RootElement.GetProperty("data");
        after.GetProperty("covered").GetArrayLength().Should().Be(2);
        after.GetProperty("uncovered").GetArrayLength().Should().Be(62);
    }

    [PostgresFact]
    public async Task TheLocationList_HasSixtyFourDistricts_AndTheIdsAreTheDatabaseRows()
    {
        using var admin = await AdminAsync();
        using var anonymous = _factory.CreateClient();

        var data = JsonDocument.Parse(await anonymous.GetStringAsync("/api/v1/locations/bd?level=district")).RootElement.GetProperty("data");
        data.GetArrayLength().Should().Be(64);
        var served = data.EnumerateArray().Select(d => d.GetProperty("id").GetGuid()).ToHashSet();
        await using var db = Db(_factory.ConnectionString);
        (await db.BdLocations.Where(l => l.Level == "district").Select(l => l.Id).ToListAsync()).Should().BeEquivalentTo(served);
    }
}

/// <summary>A9 inside the services: the order resolves the district by name, addresses validate the hierarchy.</summary>
public class BangladeshLocationBehaviourTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>, IAsyncLifetime
{
    public async Task InitializeAsync()
    {
        if (!TestInfra.PostgresAvailable) return;
        await using var db = fixture.CreateContext();
        await LocationInstall.InstallAsync(db);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<T> WithOrders<T>(Func<OrderService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        var quoter = new OrderShippingQuoter(new ShippingRateProvider(db), new ShippingOptions());   // production options: fail closed
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System, quoter, new Mechware.Infrastructure.Pricing.PriceListResolver(db)));
    }

    [PostgresTheory]
    [InlineData("Dhaka", "Dhaka")]
    [InlineData("ঢাকা", "ঢাকা")]
    [InlineData("dhaka", "Dhaka")]
    public async Task AnOrderToDhaka_ResolvesTheDhakaDistrict_AndIsPricedFromTheZone(string district, string division)
    {
        await using (var db = fixture.CreateContext())
        {
            if (!await db.ShippingZones.AnyAsync(z => z.Code == "a9-dhaka"))
            {
                db.ShippingZones.Add(new ShippingZone(Guid.NewGuid(), "a9-dhaka", "Dhaka", "ঢাকা", [BangladeshLocationSeeder.DistDhakaId], ShippingTestKit.Card()));
                await db.SaveChangesAsync();
            }
        }
        var buyer = await TestUsers.CreateAsync(fixture);
        Guid addressId;
        await using (var db = fixture.CreateContext())
        {
            var address = new Address(Guid.NewGuid(), buyer, "Plant", "Engr. Receiver", "+8801711000000", "Road 1", null, null, "Tejgaon Industrial Area", district, division, "1208", true, false);
            db.Addresses.Add(address);
            await db.SaveChangesAsync();
            addressId = address.Id;
        }
        SeededVariant v;
        await using (var db = fixture.CreateContext()) v = await CatalogTestData.SeedVariantAsync(db, 10, priceMinor: 100_000);

        var placed = await WithOrders(s => s.PlaceAsync(new PlaceOrderRequest(buyer, "key-" + Guid.NewGuid().ToString("N"), [new PlaceOrderLine(v.VariantId, 1)], ShippingAddressId: addressId)));

        placed.GrandTotalMinor.Should().Be(100_000 + 6_000);
        await using var check = fixture.CreateContext();
        var json = await check.Orders.Where(o => o.Id == placed.OrderId).Select(o => o.ShipToJson).SingleAsync();
        JsonDocument.Parse(json).RootElement.GetProperty("districtId").GetGuid().Should().Be(BangladeshLocationSeeder.DistDhakaId);
    }

    [PostgresFact]
    public async Task Addresses_AcceptARealCombination_AndRefuseAnUpazilaOfAnotherDistrict()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        UpsertAddressRequestDto Req(string division, string district, string upazila) =>
            new("Plant", "Engr. Test", "+8801711000001", "Plot 1", null, null, division, district, upazila, null);

        var ok = await IdentityTestKit.With(fixture, s => s.CreateAddressAsync(buyer, Req("Sylhet", "Sunamganj", "Chhatak"), "203.112.218.10"));
        ok.District.Should().Be("Sunamganj");
        await IdentityTestKit.With(fixture, s => s.CreateAddressAsync(buyer, Req("বরিশাল", "ভোলা", "চরফ্যাশন"), "203.112.218.10"));

        var wrongUpazila = () => IdentityTestKit.With(fixture, s => s.CreateAddressAsync(buyer, Req("Sylhet", "Sunamganj", "Teknaf"), "203.112.218.10"));
        (await wrongUpazila.Should().ThrowAsync<ValidationException>()).WithMessage("*does not belong to district*");
        var wrongDivision = () => IdentityTestKit.With(fixture, s => s.CreateAddressAsync(buyer, Req("Dhaka", "Sunamganj", "Chhatak"), "203.112.218.10"));
        (await wrongDivision.Should().ThrowAsync<ValidationException>()).WithMessage("*does not belong to division*");
    }
}
