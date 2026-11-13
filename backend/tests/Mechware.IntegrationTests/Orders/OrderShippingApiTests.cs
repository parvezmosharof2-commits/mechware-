using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>Its own database, so the zones created here never reach the other API test classes.</summary>
public sealed class ShippingApiFactory : DbBackedFactory;

/// <summary>E8.2 over HTTP: the client cannot set shipping, and the preview endpoint prices or refuses like placement.</summary>
public class OrderShippingApiTests(ShippingApiFactory factory) : IClassFixture<ShippingApiFactory>
{
    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    /// <summary>A verified buyer whose default address is in a district served by a fresh zone (60.00 up to 1 kg, 120.00 up to 5 kg).</summary>
    private async Task<HttpClient> BuyerInAZoneAsync()
    {
        string email = $"buyer.{Guid.NewGuid():N}@example.test";
        const string password = "Buyer#Test2026xyz";
        await using (var db = Db())
        {
            var district = await ShippingTestKit.ZoneAsync(db, ShippingTestKit.Card());
            var user = User.Create(email, null, new PasswordHasherService().HashPassword(password), "Test Buyer", "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
            db.Users.Add(user);
            db.Addresses.Add(ShippingTestKit.AddressIn(user.Id, district.NameEn, isDefault: true));
            await db.SaveChangesAsync();
        }

        var client = factory.CreateClient();
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { identifier = email, password });
        login.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data").GetProperty("accessToken").GetString();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private async Task<SeededVariant> SeedAsync(long price)
    {
        await using var db = Db();
        return await CatalogTestData.SeedVariantAsync(db, 50, priceMinor: price);
    }

    private static StringContent Json(string json) => new(json, Encoding.UTF8, "application/json");

    [PostgresFact]
    public async Task AClientThatSendsItsOwnShipping_StillPaysTheZonesFee()
    {
        var v = await SeedAsync(250_000);
        var client = await BuyerInAZoneAsync();

        // Every shipping-looking member a client might try, at the top, line and sub-order level.
        var tampered = $$"""
            { "shippingMinor": 0, "shipping": 0, "shippingTotal": 0, "shippingFee": 0, "freeShipping": true, "freeShippingApplied": true,
              "grandTotalMinor": 500000, "subOrders": [ { "shippingMinor": 0 } ],
              "lines": [ { "variantId": "{{v.VariantId}}", "quantity": 2, "shippingMinor": 0, "weightKg": 0.001 } ] }
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, "/api/v1/orders") { Content = Json(tampered) };
        request.Headers.Add("Idempotency-Key", "k-" + Guid.NewGuid().ToString("N"));
        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var placed = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        placed.GetProperty("grandTotalMinor").GetInt64().Should().Be(500_000 + 6_000, "2 x 0.5 kg from the database is the 60.00 band");

        var order = (await (await client.GetAsync($"/api/v1/orders/{placed.GetProperty("orderId").GetGuid()}")).Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        order.GetProperty("shippingMinor").GetInt64().Should().Be(6_000);
        order.GetProperty("subOrders")[0].GetProperty("shippingMinor").GetInt64().Should().Be(6_000);
        order.GetProperty("purchaseOrderNumber").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [PostgresFact]
    public async Task TheShippingQuoteEndpoint_PricesPerSeller_AndRefusesQuoteOnlyWith422()
    {
        var v = await SeedAsync(100_000);
        var client = await BuyerInAZoneAsync();

        using var priced = await client.PostAsync("/api/v1/orders/shipping-quote",
            Json($$"""{ "lines": [ { "variantId": "{{v.VariantId}}", "quantity": 3 } ], "shippingMinor": 0 }"""));
        priced.StatusCode.Should().Be(HttpStatusCode.OK);
        var data = (await priced.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("data");
        data.GetProperty("totalMinor").GetInt64().Should().Be(12_000);   // 1.5 kg
        data.GetProperty("freeShippingApplied").GetBoolean().Should().BeFalse();
        data.GetProperty("perSeller")[0].GetProperty("shippingMinor").GetInt64().Should().Be(12_000);

        using var tooHeavy = await client.PostAsync("/api/v1/orders/shipping-quote",
            Json($$"""{ "lines": [ { "variantId": "{{v.VariantId}}", "quantity": 11 } ] }"""));
        tooHeavy.StatusCode.Should().Be(HttpStatusCode.UnprocessableEntity);
        var error = (await tooHeavy.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("error");
        error.GetProperty("code").GetString().Should().Be("SHIPPING_QUOTE_ONLY");
        error.GetProperty("details")[0].GetProperty("field").GetString().Should().Be("ABOVE_LAST_BAND");

        using var anonymous = await factory.CreateClient().PostAsync("/api/v1/orders/shipping-quote",
            Json($$"""{ "lines": [ { "variantId": "{{v.VariantId}}", "quantity": 1 } ] }"""));
        anonymous.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }
}
