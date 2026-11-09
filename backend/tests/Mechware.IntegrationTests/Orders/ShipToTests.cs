using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// Finding #4 of the 2026-11-04 independent test: the delivery address was free JSON typed by the client, so invalid JSON
/// was a 500, two megabytes were stored, and an order could be placed with no address. The buyer now chooses a saved
/// address or a company site, and the server copies it into the order.
/// </summary>
public class ShipToTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private async Task<T> With<T>(Func<OrderService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System));
    }

    private async Task<SeededVariant> SeedAsync()
    {
        await using var db = fixture.CreateContext();
        return await CatalogTestData.SeedVariantAsync(db, 20, priceMinor: 100_000);
    }

    private async Task<int> ReservedAsync(Guid variant)
    {
        await using var db = fixture.CreateContext();
        return await db.InventoryItems.Where(i => i.VariantId == variant).SumAsync(i => i.Reserved);
    }

    private async Task<JsonElement> ShipToOfAsync(Guid orderId)
    {
        await using var db = fixture.CreateContext();
        var json = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => o.ShipToJson).SingleAsync();
        return JsonDocument.Parse(json).RootElement;
    }

    private async Task<Guid> AddAddressAsync(Guid user, bool isDefault)
    {
        await using var db = fixture.CreateContext();
        var address = new Mechware.Domain.Identity.Address(Guid.NewGuid(), user, "Plant 2", "Mr. Second Plant", "+8801811000000",
            "Road 9, Plant 2", null, null, "Savar", "Dhaka", "Dhaka", "1340", isDefault, false);
        db.Addresses.Add(address);
        await db.SaveChangesAsync();
        return address.Id;
    }

    private async Task<Guid> BuyerWithoutAddressAsync()
    {
        var user = await TestUsers.CreateAsync(fixture);
        await using var db = fixture.CreateContext();
        await db.Addresses.Where(a => a.UserId == user).ExecuteDeleteAsync();
        return user;
    }

    [PostgresFact]
    public async Task ABuyerWithNoAddress_CannotOrder_AndHoldsNoStock()
    {
        var buyer = await BuyerWithoutAddressAsync();
        var v = await SeedAsync();

        var act = () => With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 2)])));

        (await act.Should().ThrowAsync<ValidationException>()).Which.Message.Should().Contain("shipping address");
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task TheDefaultAddress_IsUsed_AndAChosenOne_IsCopiedIntoTheOrder()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        var second = await AddAddressAsync(buyer, isDefault: false);
        var v = await SeedAsync();

        var byDefault = await With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)])));
        (await ShipToOfAsync(byDefault.OrderId)).GetProperty("upazila").GetString().Should().Be("Tongi");

        var chosen = await With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], ShippingAddressId: second)));
        var shipTo = await ShipToOfAsync(chosen.OrderId);
        shipTo.GetProperty("source").GetString().Should().Be("address");
        shipTo.GetProperty("recipientName").GetString().Should().Be("Mr. Second Plant");
        shipTo.GetProperty("upazila").GetString().Should().Be("Savar");

        // Deleting the address later does not change where the placed order goes.
        await using (var db = fixture.CreateContext()) await db.Addresses.Where(a => a.Id == second).ExecuteDeleteAsync();
        (await ShipToOfAsync(chosen.OrderId)).GetProperty("upazila").GetString().Should().Be("Savar");
    }

    [PostgresFact]
    public async Task SomeoneElsesAddress_IsNotFound_AndHoldsNoStock()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        var stranger = await TestUsers.CreateAsync(fixture);
        var strangersAddress = await AddAddressAsync(stranger, isDefault: false);
        var v = await SeedAsync();

        var act = () => With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], ShippingAddressId: strangersAddress)));

        await act.Should().ThrowAsync<KeyNotFoundException>();
        (await ReservedAsync(v.VariantId)).Should().Be(0);
    }

    [PostgresFact]
    public async Task AmbiguousOrMisplacedChoices_AreRefused()
    {
        var buyer = await TestUsers.CreateAsync(fixture);
        var address = await AddAddressAsync(buyer, isDefault: false);
        var v = await SeedAsync();
        var lines = new List<PlaceOrderLine> { new(v.VariantId, 1) };

        var both = () => With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), lines, ShippingAddressId: address, ShipToSiteId: Guid.NewGuid())));
        var siteWithoutCompany = () => With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), lines, ShipToSiteId: Guid.NewGuid())));

        await both.Should().ThrowAsync<ValidationException>();
        await siteWithoutCompany.Should().ThrowAsync<ValidationException>();
    }

    [PostgresFact]
    public async Task ACompanyOrder_GoesToTheCompanysDefaultSite_AndAnotherCompanysSiteIsNotFound()
    {
        var admin = await TestUsers.CreateAsync(fixture);
        var buyer = await TestUsers.CreateAsync(fixture);
        var otherAdmin = await TestUsers.CreateAsync(fixture);
        var v = await SeedAsync();

        Guid company, otherSite;
        await using (var db = fixture.CreateContext())
        {
            var svc = new CompanyService(db);
            company = (await svc.RegisterAsync(admin, new RegisterCompanyRequest("Site Ltd", "TL-S-1", Bin(), null, null))).Id;
            await svc.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
            await svc.AddSiteAsync(admin, company, new SiteRequest("Gazipur plant", "Store Keeper", "+8801911000000", "Plot 7, BSCIC",
                null, "Kaliakair", "Gazipur", "Dhaka", "1750", IsDefaultDelivery: true));
            await svc.AdminActivateAsync(company);

            var other = (await svc.RegisterAsync(otherAdmin, new RegisterCompanyRequest("Other Ltd", "TL-S-2", Bin(), null, null))).Id;
            otherSite = (await svc.AddSiteAsync(otherAdmin, other, new SiteRequest("Rival plant", "Rival", "+8801911000001", "Rival road",
                null, "Savar", "Dhaka", "Dhaka", "1340", IsDefaultDelivery: true))).Id;
        }

        var placed = await With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], CompanyId: company)));
        var shipTo = await ShipToOfAsync(placed.OrderId);
        shipTo.GetProperty("source").GetString().Should().Be("company_site");
        shipTo.GetProperty("siteName").GetString().Should().Be("Gazipur plant");

        var rival = () => With(s => s.PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)], CompanyId: company, ShipToSiteId: otherSite)));
        await rival.Should().ThrowAsync<KeyNotFoundException>();
        (await ReservedAsync(v.VariantId)).Should().Be(1, "only the first order holds stock");
    }

    private static string Bin() => (9_000_000_000_000L + Random.Shared.NextInt64(0, 999_999_999_999L)).ToString("D13");
}
