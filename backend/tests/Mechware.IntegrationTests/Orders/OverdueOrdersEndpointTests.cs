using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>E8.3 over HTTP: the staff overdue list (permission, paging, no personal data) and a client that tries to set deadlines.</summary>
public sealed class OverdueOrdersEndpointTests : IDisposable
{
    private static readonly TotpService Totp = new();
    private readonly DefaultApiFactory _factory = new();

    public void Dispose() => _factory.Dispose();

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(_factory.ConnectionString).Options);

    private async Task<HttpClient> SignedInAsync(string email, string password, string? secret)
    {
        var client = _factory.CreateClient();
        object body = secret is null
            ? new { identifier = email, password }
            : new { identifier = email, password, totpCode = Totp.ComputeTotpCode(secret, DateTimeOffset.UtcNow) };
        var login = await client.PostAsJsonAsync("/api/v1/auth/login", body);
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString()!);
        return client;
    }

    private Task<HttpClient> AdminAsync() => SignedInAsync("admin@mechware.com.bd", "MechAdmin#2026!", "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP");
    private Task<HttpClient> BuyerAsync() => SignedInAsync("buyer@mechware.com.bd", "BuyerPass#2026!", null);

    /// <summary>Places an order over HTTP as the seeded buyer, with deadline-looking members in the body.</summary>
    private async Task<(Guid OrderId, JsonElement Placed)> PlaceAsync(HttpClient buyer)
    {
        Guid addressId;
        Guid variantId;
        await using (var db = Db())
        {
            var userId = await db.Users.AsNoTracking().Where(u => u.Email == "buyer@mechware.com.bd").Select(u => u.Id).SingleAsync();
            var district = await ShippingTestKit.DistrictAsync(db);
            var address = ShippingTestKit.AddressIn(userId, district.NameEn, isDefault: false);
            db.Addresses.Add(address);
            await db.SaveChangesAsync();
            addressId = address.Id;
            variantId = (await CatalogTestData.SeedVariantAsync(db, 20, priceMinor: 100_000)).VariantId;
        }

        var json = $$"""
            { "shippingAddressId": "{{addressId}}", "lines": [ { "variantId": "{{variantId}}", "quantity": 1 } ],
              "legalDeliveryDeadlineUtc": "2020-01-01T00:00:00Z", "courierHandoverDeadlineUtc": "2020-01-01T00:00:00Z",
              "estimatedDeliveryBy": "2020-01-01T00:00:00Z", "confirmedAtUtc": "2020-01-01T00:00:00Z", "deliveryDays": 1 }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
        request.Headers.Add("Idempotency-Key", "k-" + Guid.NewGuid().ToString("N"));
        using var response = await buyer.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var placed = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        return (placed.GetProperty("orderId").GetGuid(), placed);
    }

    [PostgresFact]
    public async Task A_client_cannot_send_deadlines_and_an_unpaid_order_has_none()
    {
        using var buyer = await BuyerAsync();
        var (orderId, _) = await PlaceAsync(buyer);

        await using (var db = Db())
        {
            var order = await db.Orders.AsNoTracking().SingleAsync(o => o.Id == orderId);
            order.LegalDeliveryDeadlineUtc.Should().BeNull();
            order.CourierHandoverDeadlineUtc.Should().BeNull();
            order.ConfirmedAtUtc.Should().BeNull();
        }

        var dto = (await (await buyer.GetAsync($"/api/v1/orders/{orderId}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        dto.GetProperty("estimatedDeliveryBy").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [PostgresFact]
    public async Task The_buyer_sees_the_stored_legal_deadline_as_estimatedDeliveryBy()
    {
        using var buyer = await BuyerAsync();
        var (orderId, _) = await PlaceAsync(buyer);
        var confirmedAt = DateTime.UtcNow.AddDays(-30);
        await using (var db = Db())
        {
            var order = await db.Orders.Include(o => o.SubOrders).SingleAsync(o => o.Id == orderId);
            order.Confirm(DeliveryDeadlines.Compute(confirmedAt, true, 5, 10, 48), null, "test", confirmedAt);
            await db.SaveChangesAsync();
        }

        var dto = (await (await buyer.GetAsync($"/api/v1/orders/{orderId}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        dto.GetProperty("estimatedDeliveryBy").GetDateTime().Should().BeCloseTo(confirmedAt.AddDays(5), TimeSpan.FromSeconds(1));
        dto.EnumerateObject().Select(p => p.Name).Should().NotContain(["legalDeliveryDeadlineUtc", "courierHandoverDeadlineUtc"]);
    }

    [PostgresFact]
    public async Task Only_staff_with_the_all_orders_permission_may_list_overdue_orders()
    {
        using var anonymous = _factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/admin/orders/overdue")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        using var buyer = await BuyerAsync();
        (await buyer.GetAsync("/api/v1/admin/orders/overdue")).StatusCode.Should().Be(HttpStatusCode.Forbidden);
        using var admin = await AdminAsync();
        (await admin.GetAsync("/api/v1/admin/orders/overdue")).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [PostgresFact]
    public async Task The_overdue_list_pages_caps_the_page_size_and_carries_no_personal_data()
    {
        using var buyer = await BuyerAsync();
        var overdue = new List<Guid>();
        for (var i = 0; i < 3; i++)
        {
            var (orderId, _) = await PlaceAsync(buyer);
            var confirmedAt = DateTime.UtcNow.AddDays(-30 - i);
            await using var db = Db();
            var order = await db.Orders.Include(o => o.SubOrders).SingleAsync(o => o.Id == orderId);
            order.Confirm(DeliveryDeadlines.Compute(confirmedAt, false, 5, 10, 48), null, "test", confirmedAt);
            await db.SaveChangesAsync();
            overdue.Add(orderId);
        }

        using var admin = await AdminAsync();
        using var page1 = await admin.GetAsync("/api/v1/admin/orders/overdue?page=1&pageSize=2");
        var raw = await page1.Content.ReadAsStringAsync();
        var data = JsonDocument.Parse(raw).RootElement.GetProperty("data");
        data.GetProperty("items").GetArrayLength().Should().Be(2);
        data.GetProperty("totalCount").GetInt32().Should().BeGreaterThanOrEqualTo(3);

        using var all = await admin.GetAsync("/api/v1/admin/orders/overdue?pageSize=500");
        var allRaw = await all.Content.ReadAsStringAsync();
        var allData = JsonDocument.Parse(allRaw).RootElement.GetProperty("data");
        allData.GetProperty("pageSize").GetInt32().Should().Be(50);
        var items = allData.GetProperty("items").EnumerateArray().ToList();
        items.Select(i => i.GetProperty("orderId").GetGuid()).Should().Contain(overdue);

        foreach (var item in items)
        {
            item.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(
                ["orderId", "orderNumber", "status", "districtId", "district", "legalDeliveryDeadlineUtc", "courierHandoverDeadlineUtc",
                 "deliveryOverdue", "handoverOverdue", "hoursOverdue"]);
        }

        // Nothing of the recipient (name, phone, street) reaches the staff list.
        allRaw.Should().NotContain("Engr. Receiver").And.NotContain("+8801711000000").And.NotContain("Road 1, Plot 2").And.NotContain("buyer@mechware.com.bd");
    }
}
