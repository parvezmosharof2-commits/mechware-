using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// F2.1 return requests over HTTP and at the service: rules (delivered only, Dhaka-day window, delivered minus claimed, non-returnable
/// categories), the IDOR matrix (same 404), idempotency, the parallel over-claim race, the staff permission matrix and the database guard.
/// </summary>
public sealed class ReturnRequestTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "ReturnsTest#2026!";
    private static int _binSeq = 7_100_000;

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private sealed record Person(HttpClient Client, Guid UserId);

    private sealed record TestOrder(Guid Id, string Number, Guid Line1, Guid Line2);

    private sealed class Buyer(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.21";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-returns";
        public bool HasPermission(string permission) => false;
    }

    private sealed class FixedClock(DateTime utc) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(utc, TimeSpan.Zero);
    }

    private async Task<Person> LoginAsync(string role)
    {
        factory.CreateClient().Dispose();
        var email = $"ret.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "Returns " + role, "en");
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

    private async Task<Guid> PlainUserAsync()
    {
        await using var db = Db();
        var u = User.Create($"rb.{Guid.NewGuid():N}@example.test", null, null, "Return Buyer");
        u.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    /// <summary>An order of <paramref name="buyer"/> with two lines (quantities <paramref name="qty1"/> and 1), delivered (an order event) at <paramref name="deliveredAt"/>.</summary>
    private async Task<TestOrder> OrderAsync(Guid buyer, Guid? company = null, string status = "Delivered", DateTime? deliveredAt = null, Guid? variant = null, int qty1 = 3)
    {
        await using var db = Db();
        var now = DateTime.UtcNow;
        var id = await CreditTestData.InsertOrderAsync(db, buyer, company, 25_000, onCredit: false, status: status, confirmedAtUtc: now.AddDays(-30), placedAtUtc: now.AddDays(-31));
        var sub = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO sub_orders (id, order_id, seller_id, sub_order_number, status, goods_net, discount_total, tax_total, shipping_total, total,
                commission_rate, commission, seller_earning, created_at)
            VALUES ({sub}, {id}, {Guid.NewGuid()}, {"S-" + Guid.NewGuid().ToString("N")[..12]}, {status}, 0, 0, 0, 0, 0, 0, 0, 0, {now})");
        var (l1, l2) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var (line, qty, v, sku) in new[] { (l1, qty1, variant ?? Guid.NewGuid(), "SKU-A"), (l2, 1, Guid.NewGuid(), "SKU-B") })
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO order_items (id, sub_order_id, variant_id, sku, title, quantity, unit_price, vat_rate, gross, discount, net, tax, total, created_at)
                VALUES ({line}, {sub}, {v}, {sku}, {"Relay " + sku}, {qty}, 1000, 0.05, {1000 * qty}, 0, {1000 * qty}, 0, {1000 * qty}, {now})");
        if (status == "Delivered") await CreditTestData.AddEventAsync(db, id, 2, "Shipped", "Delivered", deliveredAt ?? now.AddDays(-1));
        var number = await db.Orders.Where(o => o.Id == id).Select(o => o.OrderNumber).SingleAsync();
        return new TestOrder(id, number, l1, l2);
    }

    private async Task<Guid> CompanyAsync(params Guid[] members)
    {
        await using var db = Db();
        var company = Company.Register(Guid.NewGuid(), "Returns Co " + Guid.NewGuid().ToString("N")[..6], "TL-F21-" + Guid.NewGuid().ToString("N")[..6],
            Interlocked.Increment(ref _binSeq).ToString("D13"), null, null);
        company.Activate();
        db.Companies.Add(company);
        foreach (var m in members) db.CompanyMembers.Add(new CompanyMember(Guid.NewGuid(), company.Id, m, CompanyRoles.Buyer, null, null, accepted: true));
        await db.SaveChangesAsync();
        return company.Id;
    }

    private static object Body(Guid line, int qty, Guid? companyId = null, string reason = "damaged", string? note = "Arrived broken") =>
        new { lines = new[] { new { orderItemId = line, quantity = qty } }, reasonCode = reason, note, companyId };

    private static Task<HttpResponseMessage> OpenAsync(HttpClient client, string orderNumber, object body, string? key = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{orderNumber}/returns") { Content = JsonContent.Create(body) };
        if (key is not "") request.Headers.Add("Idempotency-Key", key ?? "ret-" + Guid.NewGuid().ToString("N"));
        return client.SendAsync(request);
    }

    private static async Task<JsonElement> DataAsync(HttpResponseMessage r, HttpStatusCode expected = HttpStatusCode.OK)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("data").Clone();
    }

    private static async Task<string> ErrorAsync(HttpResponseMessage r, HttpStatusCode expected)
    {
        var text = await r.Content.ReadAsStringAsync();
        r.StatusCode.Should().Be(expected, text);
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.GetProperty("error").GetProperty("code").GetString()!;
    }

    private ReturnRequestService Service(MechwareDbContext db, Guid user, DateTime? now = null, ReturnOptions? options = null) =>
        new(db, new Buyer(user), now is { } at ? new FixedClock(at) : TimeProvider.System, options);

    private static OpenReturnRequest Ask(Guid user, TestOrder order, int qty, string? key = null, Guid? line = null) =>
        new(user, order.Number, key ?? "svc-" + Guid.NewGuid().ToString("N"), null, "damaged", null, [new ReturnLineInput(line ?? order.Line1, qty)]);

    [PostgresFact]
    public async Task TheBuyer_OpensListsReadsAndWithdraws_WithAuditOutboxAndNoStore()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var order = await OrderAsync(buyer.UserId);

        var created = await OpenAsync(buyer.Client, order.Number,
            new { lines = new[] { new { orderItemId = order.Line1, quantity = 2 }, new { orderItemId = order.Line2, quantity = 1 } }, reasonCode = "wrong_item", note = "  Wrong coil voltage​ " });
        (created.Headers.CacheControl!.Private, created.Headers.CacheControl.NoStore).Should().Be((true, true));
        var r = await DataAsync(created, HttpStatusCode.Created);
        var id = r.GetProperty("id").GetGuid();
        (r.GetProperty("status").GetString(), r.GetProperty("reasonCode").GetString(), r.GetProperty("note").GetString(), r.GetProperty("orderNumber").GetString())
            .Should().Be(("requested", "wrong_item", "Wrong coil voltage", order.Number));
        r.GetProperty("companyId").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("lines").EnumerateArray().Select(l => (l.GetProperty("sku").GetString(), l.GetProperty("quantity").GetInt32()))
            .Should().Equal(("SKU-A", 2), ("SKU-B", 1));

        var list = await buyer.Client.GetAsync("/api/v1/returns");
        list.Headers.CacheControl!.NoStore.Should().BeTrue();
        (await DataAsync(list)).GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(id);
        var one = await buyer.Client.GetAsync($"/api/v1/returns/{id}");
        one.Headers.CacheControl!.Private.Should().BeTrue();
        (await DataAsync(one)).GetProperty("status").GetString().Should().Be("requested");

        var withdrawn = await DataAsync(await buyer.Client.PostAsync($"/api/v1/returns/{id}/withdraw", null));
        withdrawn.GetProperty("status").GetString().Should().Be("withdrawn");
        var again = await DataAsync(await buyer.Client.PostAsync($"/api/v1/returns/{id}/withdraw", null));
        again.GetProperty("withdrawnAtUtc").GetDateTime().Should().Be(withdrawn.GetProperty("withdrawnAtUtc").GetDateTime());

        await using var db = Db();
        (await db.AuditLogs.Where(a => a.EntityId == id.ToString() && a.Action.StartsWith("return.")).Select(a => a.Action).ToListAsync())
            .Should().BeEquivalentTo(["return.requested", "return.withdrawn"], "a second withdrawal writes nothing");
        (await db.OutboxMessages.Where(m => m.AggregateId == id).Select(m => m.EventType).ToListAsync())
            .Should().BeEquivalentTo([nameof(ReturnRequestedEvent), nameof(ReturnWithdrawnEvent)]);
    }

    [PostgresFact]
    public async Task PartialReturns_OverTwoRequests_StopAtTheDeliveredQuantity_AndAWithdrawalFreesTheUnits()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var order = await OrderAsync(buyer.UserId);   // line 1: 3 delivered
        var first = (await DataAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 2)), HttpStatusCode.Created)).GetProperty("id").GetGuid();
        (await ErrorAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 2)), HttpStatusCode.Conflict)).Should().Be("RETURN_QUANTITY_EXCEEDED");
        await DataAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 1)), HttpStatusCode.Created);
        (await ErrorAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 1)), HttpStatusCode.Conflict)).Should().Be("RETURN_QUANTITY_EXCEEDED");
        (await ErrorAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line2, 2)), HttpStatusCode.Conflict)).Should().Be("RETURN_QUANTITY_EXCEEDED");

        await DataAsync(await buyer.Client.PostAsync($"/api/v1/returns/{first}/withdraw", null));
        await DataAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 2)), HttpStatusCode.Created);
        await using var db = Db();
        (await db.ReturnRequests.Where(r => r.OrderId == order.Id && r.Status == ReturnStatus.Requested).SelectMany(r => r.Lines).SumAsync(l => l.Quantity))
            .Should().Be(3);
    }

    [PostgresFact]
    public async Task NotDelivered_OutsideTheWindow_OrBadInput_IsRefused_AndAShipmentDeliveryTimeWinsOverTheEvent()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        foreach (var status in new[] { "Confirmed", "Shipped", "OutForDelivery", "Cancelled", "PendingPayment" })
        {
            var o = await OrderAsync(buyer.UserId, status: status);
            (await ErrorAsync(await OpenAsync(buyer.Client, o.Number, Body(o.Line1, 1)), HttpStatusCode.Conflict)).Should().Be("RETURN_ORDER_NOT_DELIVERED", status);
        }
        var old = await OrderAsync(buyer.UserId, deliveredAt: DateTime.UtcNow.AddDays(-9));
        (await ErrorAsync(await OpenAsync(buyer.Client, old.Number, Body(old.Line1, 1)), HttpStatusCode.Conflict)).Should().Be("RETURN_WINDOW_CLOSED");
        await using (var db = Db())
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO shipments (id, order_id, courier_name, tracking_number, shipped_at, delivered_at, created_by, created_at)
                VALUES ({Guid.NewGuid()}, {old.Id}, 'Steadfast', 'SF-1', {DateTime.UtcNow.AddDays(-3)}, {DateTime.UtcNow.AddDays(-2)}, {buyer.UserId}, {DateTime.UtcNow})");
        await DataAsync(await OpenAsync(buyer.Client, old.Number, Body(old.Line1, 1)), HttpStatusCode.Created);

        var ok = await OrderAsync(buyer.UserId);
        (await ErrorAsync(await OpenAsync(buyer.Client, ok.Number, Body(ok.Line1, 1, reason: "broken")), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await OpenAsync(buyer.Client, ok.Number, Body(ok.Line1, 1, reason: "other", note: " ")), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await OpenAsync(buyer.Client, ok.Number, Body(ok.Line1, 0)), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await OpenAsync(buyer.Client, ok.Number, Body(old.Line1, 1)), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR", "a line of another order");
        (await ErrorAsync(await OpenAsync(buyer.Client, ok.Number, Body(ok.Line1, 1, note: new string('x', 1001))), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
    }

    [PostgresFact]
    public async Task IdempotencyKey_IsRequired_TheSameKeyAndBodyReplay_AnotherBodyIs409()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var order = await OrderAsync(buyer.UserId);
        (await ErrorAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 1), key: ""), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 1), key: "short"), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");

        var key = "rk-" + Guid.NewGuid().ToString("N");
        var first = await DataAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 3), key), HttpStatusCode.Created);
        var replay = await DataAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 3), key), HttpStatusCode.OK);
        replay.GetProperty("id").GetGuid().Should().Be(first.GetProperty("id").GetGuid(), "a replay is the first return, not an over-claim");
        (await ErrorAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 2), key), HttpStatusCode.Conflict)).Should().Be("IDEMPOTENCY_KEY_REUSED");

        // Four parallel requests with one key: one return.
        var order2 = await OrderAsync(buyer.UserId);
        var key2 = "rk-" + Guid.NewGuid().ToString("N");
        var answers = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => OpenAsync(buyer.Client, order2.Number, Body(order2.Line1, 1), key2)));
        answers.Select(a => a.StatusCode).Should().OnlyContain(s => s == HttpStatusCode.Created || s == HttpStatusCode.OK || s == HttpStatusCode.Conflict);
        answers.Count(a => a.StatusCode == HttpStatusCode.Created).Should().Be(1);
        await using var db = Db();
        (await db.ReturnRequests.CountAsync(r => r.OrderId == order2.Id)).Should().Be(1);
    }

    [PostgresFact]
    public async Task Idor_AnythingTheCallerMayNotSee_IsTheSame404()
    {
        var owner = await LoginAsync(Permissions.Roles.Buyer);
        var other = await LoginAsync(Permissions.Roles.Buyer);
        var order = await OrderAsync(owner.UserId);
        var mine = (await DataAsync(await OpenAsync(owner.Client, order.Number, Body(order.Line1, 1)), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        (await ErrorAsync(await OpenAsync(other.Client, order.Number, Body(order.Line1, 1)), HttpStatusCode.NotFound)).Should().Be("NOT_FOUND");
        (await other.Client.GetAsync($"/api/v1/returns/{mine}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await other.Client.PostAsync($"/api/v1/returns/{mine}/withdraw", null)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await DataAsync(await other.Client.GetAsync("/api/v1/returns"))).GetProperty("totalCount").GetInt32().Should().Be(0);
        (await owner.Client.GetAsync($"/api/v1/returns/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        foreach (var number in new[] { "MW-000000-ZZZZZZ", "not-a-number", "MW-261101-abc" })
            (await OpenAsync(owner.Client, number, Body(order.Line1, 1))).StatusCode.Should().Be(HttpStatusCode.NotFound, number);
        (await OpenAsync(owner.Client, order.Number, Body(order.Line2, 1, companyId: await CompanyAsync(owner.UserId)))).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "a personal order named as a company order");
        (await owner.Client.GetAsync($"/api/v1/admin/returns/{mine}")).StatusCode.Should().Be(HttpStatusCode.Forbidden);

        // Company order of company A, placed by the owner, who is also a member of company C; a colleague is a member of A.
        var colleague = await LoginAsync(Permissions.Roles.Buyer);
        var companyA = await CompanyAsync(owner.UserId, colleague.UserId);
        var companyC = await CompanyAsync(owner.UserId);
        var companyOrder = await OrderAsync(owner.UserId, company: companyA);
        (await OpenAsync(owner.Client, companyOrder.Number, Body(companyOrder.Line1, 1))).StatusCode.Should().Be(HttpStatusCode.NotFound, "the company is not named");
        (await OpenAsync(owner.Client, companyOrder.Number, Body(companyOrder.Line1, 1, companyC))).StatusCode.Should().Be(HttpStatusCode.NotFound, "another company of the caller");
        (await OpenAsync(colleague.Client, companyOrder.Number, Body(companyOrder.Line1, 1, companyA))).StatusCode.Should().Be(HttpStatusCode.NotFound, "a colleague who did not place it");
        var companyReturn = await DataAsync(await OpenAsync(owner.Client, companyOrder.Number, Body(companyOrder.Line1, 1, companyA)), HttpStatusCode.Created);
        companyReturn.GetProperty("companyId").GetGuid().Should().Be(companyA);
        (await colleague.Client.GetAsync($"/api/v1/returns/{companyReturn.GetProperty("id").GetGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);

        await using (var db = Db())
            await db.CompanyMembers.Where(m => m.CompanyId == companyA && m.UserId == owner.UserId).ExecuteUpdateAsync(s => s.SetProperty(m => m.IsActive, false));
        (await OpenAsync(owner.Client, companyOrder.Number, Body(companyOrder.Line2, 1, companyA))).StatusCode.Should().Be(HttpStatusCode.NotFound, "the owner left the company");
    }

    [PostgresFact]
    public async Task StaffReads_NeedOrdersViewAll_SupportFinanceAdmin200_OthersForbidden_WithFilters()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var company = await CompanyAsync(buyer.UserId);
        var order = await OrderAsync(buyer.UserId, company: company);
        var id = (await DataAsync(await OpenAsync(buyer.Client, order.Number, Body(order.Line1, 1, company)), HttpStatusCode.Created)).GetProperty("id").GetGuid();

        using var anonymous = factory.CreateClient();
        (await anonymous.GetAsync("/api/v1/admin/returns")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync($"/api/v1/admin/returns/{id}")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        (await anonymous.GetAsync("/api/v1/returns")).StatusCode.Should().Be(HttpStatusCode.Unauthorized);
        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Moderator })
        {
            var who = await LoginAsync(role);
            (await who.Client.GetAsync("/api/v1/admin/returns")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
            (await who.Client.GetAsync($"/api/v1/admin/returns/{id}")).StatusCode.Should().Be(HttpStatusCode.Forbidden, role);
        }
        foreach (var role in new[] { Permissions.Roles.Support, Permissions.Roles.Finance, Permissions.Roles.Admin })
        {
            var staff = await LoginAsync(role);
            var detail = await staff.Client.GetAsync($"/api/v1/admin/returns/{id}");
            detail.Headers.CacheControl!.NoStore.Should().BeTrue();
            (await DataAsync(detail)).GetProperty("requestedBy").GetGuid().Should().Be(buyer.UserId, role);
            var byFilters = await DataAsync(await staff.Client.GetAsync($"/api/v1/admin/returns?status=requested&companyId={company}&orderNumber={order.Number[..9]}"));
            byFilters.GetProperty("items").EnumerateArray().Select(i => i.GetProperty("id").GetGuid()).Should().Equal(id);
            (await DataAsync(await staff.Client.GetAsync($"/api/v1/admin/returns?status=withdrawn&companyId={company}"))).GetProperty("totalCount").GetInt32().Should().Be(0);
            (await ErrorAsync(await staff.Client.GetAsync("/api/v1/admin/returns?status=lost"), HttpStatusCode.BadRequest)).Should().Be("VALIDATION_ERROR");
            (await DataAsync(await staff.Client.GetAsync("/api/v1/admin/returns?pageSize=500"))).GetProperty("pageSize").GetInt32().Should().Be(50);
            (await staff.Client.GetAsync($"/api/v1/admin/returns/{Guid.NewGuid()}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        }
    }

    [PostgresFact]
    public async Task TheWindow_EndsAtDhakaMidnight_AfterTheSeventhDay()
    {
        var user = await PlainUserAsync();
        var delivered = new DateTime(2026, 11, 1, 17, 30, 0, DateTimeKind.Utc);   // 23:30 Dhaka on 1 Nov
        var inTime = await OrderAsync(user, deliveredAt: delivered);
        var late = await OrderAsync(user, deliveredAt: delivered);
        await using (var db = Db())
            (await Service(db, user, new DateTime(2026, 11, 8, 17, 59, 59, DateTimeKind.Utc)).OpenAsync(Ask(user, inTime, 1))).Return.Status.Should().Be("requested");
        await using (var db = Db())
            (await FluentActions.Invoking(() => Service(db, user, new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc)).OpenAsync(Ask(user, late, 1)))
                .Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("RETURN_WINDOW_CLOSED");
        await using (var db = Db())
            (await Service(db, user, new DateTime(2026, 11, 9, 17, 0, 0, DateTimeKind.Utc), new ReturnOptions { WindowDays = 8 }).OpenAsync(Ask(user, late, 1)))
                .Return.Status.Should().Be("requested", "the window comes from the options");
    }

    [PostgresFact]
    public async Task ALineInANonReturnableCategory_OrItsSubCategory_IsRefused()
    {
        var user = await PlainUserAsync();
        Guid variant;
        string slug;
        await using (var db = Db())
        {
            var seeded = await CatalogTestData.SeedVariantAsync(db, onHand: 5);
            variant = seeded.VariantId;
            slug = await db.Products.Where(p => p.Id == seeded.ProductId).Join(db.Categories, p => p.CategoryId, c => c.Id, (_, c) => c.Slug).SingleAsync();
        }
        var order = await OrderAsync(user, variant: variant);
        var options = new ReturnOptions { NonReturnableCategorySlugs = $"cables, {slug.ToUpperInvariant()}" };
        await using (var db = Db())
            (await FluentActions.Invoking(() => Service(db, user, options: options).OpenAsync(Ask(user, order, 1)))
                .Should().ThrowAsync<UnprocessableException>()).Which.Code.Should().Be("RETURN_LINE_NOT_RETURNABLE");
        await using (var db = Db())
            (await Service(db, user, options: options).OpenAsync(Ask(user, order, 1, line: order.Line2))).Return.Lines.Should().ContainSingle();
        await using (var db = Db())
            (await Service(db, user).OpenAsync(Ask(user, order, 1))).Return.Status.Should().Be("requested", "no category is blocked by default");
    }

    [PostgresFact]
    public async Task ParallelRequests_ForTheSameUnits_ExactlyOneWins_FourRounds()
    {
        for (var round = 0; round < 4; round++)
        {
            var user = await PlainUserAsync();
            var order = await OrderAsync(user, qty1: 2);
            using var start = new SemaphoreSlim(0);
            var tasks = Enumerable.Range(0, 6).Select(async _ =>
            {
                await using var db = Db();
                var service = Service(db, user);
                await start.WaitAsync();
                try
                {
                    await service.OpenAsync(Ask(user, order, 2));
                    return "ok";
                }
                catch (ConflictException e)
                {
                    return e.Code;
                }
            }).ToList();
            start.Release(6);
            var results = await Task.WhenAll(tasks);
            results.Count(r => r == "ok").Should().Be(1, $"round {round}: {string.Join(",", results)}");
            results.Where(r => r != "ok").Should().OnlyContain(r => r == "RETURN_QUANTITY_EXCEEDED");
            await using var check = Db();
            (await check.ReturnRequests.Where(r => r.OrderId == order.Id).SelectMany(r => r.Lines).SumAsync(l => l.Quantity)).Should().Be(2);
        }
    }

    [PostgresFact]
    public async Task TheDatabase_RefusesAnOverClaim_ALineOfAnotherOrder_AndReopeningAWithdrawnReturn()
    {
        var user = await PlainUserAsync();
        var order = await OrderAsync(user, qty1: 2);
        var other = await OrderAsync(user);
        await using var db = Db();
        var request = (await Service(db, user).OpenAsync(Ask(user, order, 2))).Return.Id;

        async Task<string?> Sql(FormattableString sql)
        {
            try
            {
                await db.Database.ExecuteSqlInterpolatedAsync(sql);
                return null;
            }
            catch (PostgresException e)
            {
                return e.SqlState;
            }
        }

        (await Sql($"INSERT INTO return_request_lines (id, return_request_id, order_item_id, quantity, created_at) VALUES ({Guid.NewGuid()}, {request}, {order.Line2}, 2, now())"))
            .Should().Be(PostgresErrorCodes.CheckViolation, "line 2 delivered 1");
        (await Sql($"UPDATE return_request_lines SET quantity = 3 WHERE return_request_id = {request}")).Should().Be(PostgresErrorCodes.CheckViolation);
        (await Sql($"INSERT INTO return_request_lines (id, return_request_id, order_item_id, quantity, created_at) VALUES ({Guid.NewGuid()}, {request}, {other.Line2}, 1, now())"))
            .Should().Be(PostgresErrorCodes.CheckViolation, "a line of another order");
        (await Sql($"UPDATE return_requests SET status = 'Withdrawn', withdrawn_at = now() WHERE id = {request}")).Should().BeNull();
        (await Sql($"UPDATE return_requests SET status = 'Requested', withdrawn_at = NULL WHERE id = {request}")).Should().Be(PostgresErrorCodes.CheckViolation);
        (await Sql($"UPDATE return_requests SET reason = 'Other', note = NULL WHERE id = {request}")).Should().Be(PostgresErrorCodes.CheckViolation);
        (await Sql($"UPDATE return_requests SET withdrawn_at = NULL WHERE id = {request}")).Should().Be(PostgresErrorCodes.CheckViolation);
    }
}
