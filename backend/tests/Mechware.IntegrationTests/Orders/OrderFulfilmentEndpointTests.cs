using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// F1.1 over HTTP: <c>POST /api/v1/admin/orders/{id}/processing|packed|ship|delivered</c> need <c>orders.fulfil</c> (Support, Admin) and the
/// order's <c>rowVersion</c>; the shipment shows on the staff detail and (whitelisted) on the buyer's order; shipped counts as handed over.
/// </summary>
public sealed class OrderFulfilmentEndpointTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "FulfilStaff#2026!";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString).Options);

    private sealed record Person(HttpClient Client, Guid UserId);

    private async Task<Person> LoginAsync(string role)
    {
        factory.CreateClient().Dispose();   // starts the host (its Development seeding runs once) before the account is written
        var email = $"ful.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Fulfil " + role, "en");
            user.VerifyEmail(DateTime.UtcNow);
            user.AssignRole(Guid.NewGuid(), role);
            user.ConfigureTwoFactorSecret(TwoFactorSecrets.SealSecret(user.Id, Totp), TwoFactorSecrets.SealRecoveryCodes(user.Id, "[]"), DateTime.UtcNow);
            user.EnableTwoFactor(DateTime.UtcNow);
            db.Users.Add(user);
            await db.SaveChangesAsync();
            id = user.Id;
        }

        var client = factory.CreateClient();
        var login = await client.PostAsJsonAsync("/api/v1/auth/login",
            new { identifier = email, password = Password, totpCode = IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow) });
        login.StatusCode.Should().Be(HttpStatusCode.OK, await login.Content.ReadAsStringAsync());
        using var doc = JsonDocument.Parse(await login.Content.ReadAsStringAsync());
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", doc.RootElement.GetProperty("data").GetProperty("accessToken").GetString());
        return new Person(client, id);
    }

    private async Task<string> CodeAsync(Person staff)
    {
        await using var db = Db();
        await db.Users.Where(u => u.Id == staff.UserId).ExecuteUpdateAsync(s => s.SetProperty(u => u.LastUsedTotpTimeStep, (long?)null));
        return IdentityTestKit.Totp.ComputeTotpCode(Totp, DateTimeOffset.UtcNow);
    }

    /// <summary>A paid (not credit) order of <paramref name="buyer"/> (a fresh user when null) stored in <paramref name="status"/>.</summary>
    private async Task<Guid> OrderAsync(string status, Guid? buyer = null)
    {
        await using var db = Db();
        if (buyer is null)
        {
            var owner = User.Create($"owner.{Guid.NewGuid():N}@example.test", null, null, "Owner");
            owner.VerifyEmail(DateTime.UtcNow);
            db.Users.Add(owner);
            await db.SaveChangesAsync();
            buyer = owner.Id;
        }
        return await CreditTestData.InsertOrderAsync(db, buyer.Value, null, 25_000, onCredit: false, status: status, confirmedAtUtc: DateTime.UtcNow.AddHours(-1));
    }

    private async Task<OrderStatus> StatusOf(Guid order)
    {
        await using var db = Db();
        return await db.Orders.Where(o => o.Id == order).Select(o => o.Status).SingleAsync();
    }

    private async Task<uint> VersionOf(Guid order)
    {
        await using var db = Db();
        return await db.Orders.Where(o => o.Id == order).Select(o => EF.Property<uint>(o, "xmin")).SingleAsync();
    }

    private static Task<HttpResponseMessage> PostAsync(HttpClient client, Guid order, string step, object body, string? code = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/admin/orders/{order}/{step}") { Content = JsonContent.Create(body) };
        if (code is not null) request.Headers.Add("X-Step-Up-Code", code);
        return client.SendAsync(request);
    }

    private static object Ship(uint version, string courier = "Steadfast Courier", string tracking = "SF-2026-0001", string? url = "https://steadfast.com.bd/t/SF-2026-0001") =>
        new { rowVersion = version, courierName = courier, trackingNumber = tracking, trackingUrl = url };

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ErrorCodeAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private static readonly string[] Steps = ["processing", "packed", "ship", "delivered"];

    [PostgresFact]
    public async Task Fulfil_ByBuyerSellerModeratorOrFinance_Is403_AnonymousIs401_SupportAndAdminMay()
    {
        var order = await OrderAsync("Confirmed");
        var version = await VersionOf(order);
        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Moderator, Permissions.Roles.Finance })
        {
            var who = await LoginAsync(role);
            foreach (var step in Steps)
                (await PostAsync(who.Client, order, step, Ship(version))).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} {step}");
        }
        using var anonymous = factory.CreateClient();
        foreach (var step in Steps)
            (await PostAsync(anonymous, order, step, Ship(version))).StatusCode.Should().Be(HttpStatusCode.Unauthorized, step);
        (await StatusOf(order)).Should().Be(OrderStatus.Confirmed);

        var support = await LoginAsync(Permissions.Roles.Support);
        var admin = await LoginAsync(Permissions.Roles.Admin);
        var done = await DataAsync(await PostAsync(support.Client, order, "processing", new { rowVersion = version }));
        await DataAsync(await PostAsync(admin.Client, order, "packed", new { rowVersion = done.GetProperty("rowVersion").GetUInt32() }));
        (await StatusOf(order)).Should().Be(OrderStatus.Packed);
    }

    [PostgresFact]
    public async Task TheWholePath_ConfirmedToDelivered_ShowsTheShipmentToStaffAndToTheBuyer_WithAuditAndOutbox()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var order = await OrderAsync("Confirmed", buyer.UserId);
        var support = await LoginAsync(Permissions.Roles.Support);

        var detail = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/orders/{order}"));
        detail.GetProperty("shipments").GetArrayLength().Should().Be(0);
        var v = detail.GetProperty("rowVersion").GetUInt32();
        v.Should().Be(await VersionOf(order));

        v = (await DataAsync(await PostAsync(support.Client, order, "processing", new { rowVersion = v }))).GetProperty("rowVersion").GetUInt32();
        v = (await DataAsync(await PostAsync(support.Client, order, "packed", new { rowVersion = v }))).GetProperty("rowVersion").GetUInt32();
        var shipped = await DataAsync(await PostAsync(support.Client, order, "ship",
            Ship(v, courier: " Steadfast​ Courier ", url: "https://steadfast.com.bd/t/SF-2026-0001")));
        shipped.GetProperty("status").GetString().Should().Be("shipped");
        var s = shipped.GetProperty("shipments").EnumerateArray().Single();
        (s.GetProperty("courierName").GetString(), s.GetProperty("trackingNumber").GetString(), s.GetProperty("createdBy").GetGuid())
            .Should().Be(("Steadfast Courier", "SF-2026-0001", support.UserId));
        v = shipped.GetProperty("rowVersion").GetUInt32();

        // The buyer sees courier, number, link and dates only: no shipment id, no staff id.
        var mine = await DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/{order}"));
        mine.GetProperty("status").GetString().Should().Be("shipped");
        var view = mine.GetProperty("shipments").EnumerateArray().Single();
        view.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["courierName", "trackingNumber", "trackingUrl", "shippedAtUtc", "deliveredAtUtc"]);
        (view.GetProperty("trackingUrl").GetString(), view.GetProperty("deliveredAtUtc").ValueKind)
            .Should().Be(("https://steadfast.com.bd/t/SF-2026-0001", JsonValueKind.Null));
        (await buyer.Client.GetStringAsync($"/api/v1/orders/{order}")).Should().NotContain(support.UserId.ToString()).And.NotContain(s.GetProperty("id").GetString());

        var delivered = await DataAsync(await PostAsync(support.Client, order, "delivered", new { rowVersion = v }));
        delivered.GetProperty("status").GetString().Should().Be("delivered");
        detail = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/orders/{order}"));
        var staffView = detail.GetProperty("shipments").EnumerateArray().Single();
        staffView.GetProperty("deliveredAtUtc").ValueKind.Should().Be(JsonValueKind.String);
        detail.GetProperty("events").EnumerateArray().Select(e => e.GetProperty("toStatus").GetString())
            .Should().ContainInOrder("processing", "packed", "shipped", "delivered");
        (await DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/{order}"))).GetProperty("shipments")[0].GetProperty("deliveredAtUtc").ValueKind
            .Should().Be(JsonValueKind.String);

        await using var db = Db();
        (await db.AuditLogs.Where(a => a.EntityId == order.ToString() && a.ActorId == support.UserId && a.Action.StartsWith("order.")).Select(a => a.Action).ToListAsync())
            .Should().BeEquivalentTo(["order.processing_started", "order.packed", "order.shipped", "order.delivered"]);
        (await db.OutboxMessages.Where(m => m.AggregateId == order).Select(m => m.EventType).ToListAsync())
            .Should().BeEquivalentTo([nameof(OrderProcessingStartedEvent), nameof(OrderPackedEvent), nameof(OrderShippedEvent), nameof(OrderDeliveredEvent)]);
    }

    [PostgresFact]
    public async Task EachMove_FromTheWrongStatus_Is409_AndChangesNothing()
    {
        var admin = await LoginAsync(Permissions.Roles.Admin);
        foreach (var (status, step) in new[]
                 {
                     ("PendingPayment", "processing"), ("Processing", "processing"), ("Confirmed", "packed"), ("Shipped", "packed"),
                     ("Processing", "ship"), ("Cancelled", "ship"), ("Shipped", "ship"), ("Delivered", "ship"), ("Packed", "delivered"),
                     ("Cancelled", "delivered"), ("Delivered", "delivered")
                 })
        {
            var order = await OrderAsync(status);
            (await ErrorCodeAsync(await PostAsync(admin.Client, order, step, Ship(await VersionOf(order))), HttpStatusCode.Conflict))
                .Should().Be("ORDER_INVALID_TRANSITION", $"{step} from {status}");
            (await StatusOf(order)).ToString().Should().Be(status);
        }

        await using var db = Db();
        (await db.Set<Shipment>().CountAsync(x => x.CreatedBy == admin.UserId)).Should().Be(0);
        (await db.AuditLogs.CountAsync(a => a.ActorId == admin.UserId && a.Action.StartsWith("order."))).Should().Be(0);
        (await PostAsync(admin.Client, Guid.NewGuid(), "processing", new { rowVersion = 1u })).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [PostgresFact]
    public async Task AStaleOrMissingRowVersion_IsRefused()
    {
        var support = await LoginAsync(Permissions.Roles.Support);
        var order = await OrderAsync("Confirmed");
        var v1 = await VersionOf(order);
        (await PostAsync(support.Client, order, "processing", new { })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await ErrorCodeAsync(await PostAsync(support.Client, order, "processing", new { rowVersion = v1 + 1 }), HttpStatusCode.Conflict))
            .Should().Be("ORDER_CHANGED");

        await DataAsync(await PostAsync(support.Client, order, "processing", new { rowVersion = v1 }));
        // Someone else moved it on: packing with the version read before is refused although Processing → Packed is allowed.
        (await ErrorCodeAsync(await PostAsync(support.Client, order, "packed", new { rowVersion = v1 }), HttpStatusCode.Conflict)).Should().Be("ORDER_CHANGED");
        (await StatusOf(order)).Should().Be(OrderStatus.Processing);

        var packed = await OrderAsync("Packed");
        var v = await VersionOf(packed);
        await using (var db = Db()) await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET updated_at = now() WHERE id = {packed}");
        (await ErrorCodeAsync(await PostAsync(support.Client, packed, "ship", Ship(v)), HttpStatusCode.Conflict)).Should().Be("ORDER_CHANGED");
        (await StatusOf(packed)).Should().Be(OrderStatus.Packed);
    }

    [PostgresFact]
    public async Task Ship_WithBadFields_Is400_AndTheOrderStaysPacked()
    {
        var support = await LoginAsync(Permissions.Roles.Support);
        var order = await OrderAsync("Packed");
        var v = await VersionOf(order);
        foreach (var body in new[]
                 {
                     Ship(v, courier: "A"), Ship(v, courier: new string('x', 81)), Ship(v, courier: "​​"),
                     Ship(v, tracking: ""), Ship(v, tracking: "SF 1"), Ship(v, tracking: "SF<1>"), Ship(v, tracking: new string('1', 61)),
                     Ship(v, url: "http://steadfast.com.bd/t/1"), Ship(v, url: "https://steadfast.com.bd.evil.example/t"),
                     Ship(v, url: "https://evil.example/t"), Ship(v, url: "javascript:alert(1)")
                 })
            (await PostAsync(support.Client, order, "ship", body)).StatusCode.Should().Be(HttpStatusCode.BadRequest, JsonSerializer.Serialize(body));
        (await PostAsync(support.Client, order, "ship", new { rowVersion = v, trackingNumber = "SF-1" })).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await StatusOf(order)).Should().Be(OrderStatus.Packed);

        var ok = await DataAsync(await PostAsync(support.Client, order, "ship", Ship(v, courier: "Pathao", tracking: "PA-9", url: null)));
        ok.GetProperty("shipments")[0].GetProperty("trackingUrl").ValueKind.Should().Be(JsonValueKind.Null);
    }

    [PostgresFact]
    public async Task CancelAndShip_InParallel_HaveExactlyOneWinner_AndNever500()
    {
        var support = await LoginAsync(Permissions.Roles.Support);
        var admin = await LoginAsync(Permissions.Roles.Admin);
        for (var round = 0; round < 4; round++)
        {
            var order = await OrderAsync("Packed");
            var v = await VersionOf(order);
            var code = await CodeAsync(support);
            var cancel = PostAsync(support.Client, order, "cancel", new { reason = "customer cancelled by phone" }, code);
            var ship = PostAsync(admin.Client, order, "ship", Ship(v, tracking: $"SF-R{round}"));
            var results = await Task.WhenAll(cancel, ship);

            results.Select(r => (int)r.StatusCode).Should().OnlyContain(c => c == 200 || c == 409, $"round {round}");
            results.Count(r => r.StatusCode == HttpStatusCode.OK).Should().Be(1, $"round {round}");
            var status = await StatusOf(order);
            await using var db = Db();
            var shipments = await db.Set<Shipment>().CountAsync(s => s.OrderId == order);
            if (results[0].StatusCode == HttpStatusCode.OK) (status, shipments).Should().Be((OrderStatus.Cancelled, 0));
            else
            {
                (status, shipments).Should().Be((OrderStatus.Shipped, 1));
                (await ErrorCodeAsync(results[0], HttpStatusCode.Conflict)).Should().Be("ORDER_NOT_CANCELLABLE");
            }
        }
    }

    [PostgresFact]
    public async Task Shipped_CountsAsHandedOver_AndDelivered_ClosesTheDeliveryDeadline()
    {
        var support = await LoginAsync(Permissions.Roles.Support);
        var order = await OrderAsync("Packed");
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"UPDATE orders SET courier_handover_deadline_at = {DateTime.UtcNow.AddHours(-2)}, legal_delivery_deadline_at = {DateTime.UtcNow.AddDays(3)} WHERE id = {order}");

        async Task<JsonElement?> OverdueRow()
        {
            var page = await DataAsync(await support.Client.GetAsync("/api/v1/admin/orders/overdue?pageSize=50"));
            return page.GetProperty("items").EnumerateArray().Where(i => i.GetProperty("orderId").GetGuid() == order).Select(i => (JsonElement?)i).FirstOrDefault();
        }
        async Task<bool> InStaffOverdueList() =>
            (await DataAsync(await support.Client.GetAsync("/api/v1/admin/orders?overdue=true&pageSize=50"))).GetProperty("items").EnumerateArray()
                .Any(i => i.GetProperty("id").GetGuid() == order);

        (await OverdueRow())!.Value.GetProperty("handoverOverdue").GetBoolean().Should().BeTrue();
        (await InStaffOverdueList()).Should().BeTrue();

        var shipped = await DataAsync(await PostAsync(support.Client, order, "ship", Ship(await VersionOf(order))));
        (await OverdueRow()).Should().BeNull("a shipped order has been handed to the courier");
        (await InStaffOverdueList()).Should().BeFalse();

        // Past the delivery deadline while shipped: late for delivery only, until it is delivered.
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET legal_delivery_deadline_at = {DateTime.UtcNow.AddHours(-1)} WHERE id = {order}");
        var late = (await OverdueRow())!.Value;
        (late.GetProperty("deliveryOverdue").GetBoolean(), late.GetProperty("handoverOverdue").GetBoolean()).Should().Be((true, false));
        await DataAsync(await PostAsync(support.Client, order, "delivered", new { rowVersion = await VersionOf(order) }));
        (await OverdueRow()).Should().BeNull();
        shipped.GetProperty("status").GetString().Should().Be("shipped");
    }
}
