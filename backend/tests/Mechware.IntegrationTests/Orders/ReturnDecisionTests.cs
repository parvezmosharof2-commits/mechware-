using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Application.Inventory;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Inventory;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.IntegrationTests.Identity;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// F2.3a staff return moves: approve / reject / received / inspect over HTTP and at the service, every refusal, the rowVersion, the
/// permission matrix, inspection limits, the restock against the order's deduction (partial, zero, two returns of one variant, the cap),
/// approve ∥ withdraw, the crash window finished by the stock sweep, the buyer whitelist and the database guard.
/// </summary>
public sealed class ReturnDecisionTests(DefaultApiFactory factory) : IClassFixture<DefaultApiFactory>
{
    private const string Totp = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
    private const string Password = "ReturnsF23a#2026!";

    private MechwareDbContext Db() => new(new DbContextOptionsBuilder<MechwareDbContext>().UseNpgsql(factory.ConnectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor()).Options);

    private sealed record Person(HttpClient Client, Guid UserId);

    private sealed record TestOrder(Guid Id, string Number, Guid Line1, Guid Line2, Guid Variant1, Guid Variant2);

    private sealed class Actor(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.23";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-f23a";
        public bool HasPermission(string permission) => true;
    }

    /// <summary>Every restock fails (the crash window: the inspection committed, the stock step did not run).</summary>
    private sealed class RestockFails(IInventoryService inner) : IInventoryService
    {
        public Task<RestockResult> RestockAsync(RestockRequest request, CancellationToken ct = default) => throw new TimeoutException("the database went away");
        public Task<ReserveStockResult> ReserveAsync(ReserveStockRequest request, CancellationToken ct = default) => inner.ReserveAsync(request, ct);
        public Task<int> CommitAsync(Guid referenceId, CancellationToken ct = default) => inner.CommitAsync(referenceId, ct);
        public Task<int> CancelAsync(Guid referenceId, CancellationToken ct = default) => inner.CancelAsync(referenceId, ct);
        public Task<int> ReleaseExpiredAsync(DateTime nowUtc, int batchSize = 500, CancellationToken ct = default) => inner.ReleaseExpiredAsync(nowUtc, batchSize, ct);
    }

    private async Task<Person> LoginAsync(string role)
    {
        factory.CreateClient().Dispose();
        var email = $"f23.{Guid.NewGuid():N}"[..24] + "@mechware.com.bd";
        Guid id;
        await using (var db = Db())
        {
            var user = User.Create(email, null, IdentityTestKit.Hasher.HashPassword(Password), "F23a " + role, "en");
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
        var u = User.Create($"f23.{Guid.NewGuid():N}@example.test", null, null, "F23a User");
        u.VerifyEmail(DateTime.UtcNow);
        db.Users.Add(u);
        await db.SaveChangesAsync();
        return u.Id;
    }

    private static InventoryService Inventory(MechwareDbContext db, Guid? actor = null) =>
        new(db, new NoopLockProvider(), TimeProvider.System, user: actor is { } a ? new Actor(a) : null);

    /// <summary>
    /// A delivered order of <paramref name="buyer"/>: line 1 = <paramref name="qty1"/> of a real variant (10 on hand before the order), line 2 = 1 of
    /// another real variant. The order's holds took <paramref name="deducted1"/> (default all) of line 1 and 1 of line 2 from stock (committed).
    /// </summary>
    private async Task<TestOrder> OrderAsync(Guid buyer, int qty1 = 3, int? deducted1 = null)
    {
        await using var db = Db();
        var now = DateTime.UtcNow;
        var v1 = await CatalogTestData.SeedVariantAsync(db, onHand: 10);
        var v2 = await CatalogTestData.SeedVariantAsync(db, onHand: 10);
        var id = await CreditTestData.InsertOrderAsync(db, buyer, null, 25_000, onCredit: false, status: "Delivered", confirmedAtUtc: now.AddDays(-3), placedAtUtc: now.AddDays(-4));
        var sub = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($@"
            INSERT INTO sub_orders (id, order_id, seller_id, sub_order_number, status, goods_net, discount_total, tax_total, shipping_total, total,
                commission_rate, commission, seller_earning, created_at)
            VALUES ({sub}, {id}, {Guid.NewGuid()}, {"S-" + Guid.NewGuid().ToString("N")[..12]}, 'Delivered', 0, 0, 0, 0, 0, 0, 0, 0, {now})");
        var (l1, l2) = (Guid.NewGuid(), Guid.NewGuid());
        foreach (var (line, qty, v, sku) in new[] { (l1, qty1, v1.VariantId, "SKU-A"), (l2, 1, v2.VariantId, "SKU-B") })
            await db.Database.ExecuteSqlInterpolatedAsync($@"
                INSERT INTO order_items (id, sub_order_id, variant_id, sku, title, quantity, unit_price, vat_rate, gross, discount, net, tax, total, created_at)
                VALUES ({line}, {sub}, {v}, {sku}, {"Contactor " + sku}, {qty}, 1000, 0.05, {1000 * qty}, 0, {1000 * qty}, 0, {1000 * qty}, {now})");
        await CreditTestData.AddEventAsync(db, id, 2, "Shipped", "Delivered", now.AddDays(-1));
        var inventory = Inventory(db);
        (await inventory.ReserveAsync(new ReserveStockRequest(id, [new ReservationLine(v1.VariantId, deducted1 ?? qty1), new ReservationLine(v2.VariantId, 1)])))
            .Succeeded.Should().BeTrue();
        (await inventory.CommitAsync(id)).Should().BeGreaterThan(0);
        var number = await db.Orders.Where(o => o.Id == id).Select(o => o.OrderNumber).SingleAsync();
        return new TestOrder(id, number, l1, l2, v1.VariantId, v2.VariantId);
    }

    private async Task<int> OnHandAsync(Guid variant)
    {
        await using var db = Db();
        return await db.InventoryItems.Where(i => i.VariantId == variant).SumAsync(i => i.OnHand);
    }

    private ReturnRequestService Service(MechwareDbContext db, Guid user, IInventoryService? inventory = null) =>
        new(db, new Actor(user), TimeProvider.System, null, inventory ?? Inventory(db, user));

    /// <summary>A return of the order's lines (both by default), opened by its buyer at the service.</summary>
    private async Task<ReturnRequestDto> OpenAsync(Guid buyer, TestOrder order, int qty1 = 3, bool line2 = true)
    {
        await using var db = Db();
        List<ReturnLineInput> lines = [new(order.Line1, qty1)];
        if (line2) lines.Add(new(order.Line2, 1));
        return (await Service(db, buyer).OpenAsync(new OpenReturnRequest(buyer, order.Number, "f23-" + Guid.NewGuid().ToString("N"), null, "damaged", null, lines))).Return;
    }

    private async Task<StaffReturnDto> StaffAsync(Guid staff, Guid returnId)
    {
        await using var db = Db();
        return await Service(db, staff).GetStaffAsync(returnId);
    }

    private async Task<StaffReturnDto> AsStaffAsync(Guid staff, Func<ReturnRequestService, Task<StaffReturnDto>> move, IInventoryService? inventory = null)
    {
        await using var db = Db();
        return await move(Service(db, staff, inventory));
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

    private static uint Version(JsonElement e) => e.GetProperty("rowVersion").GetUInt32();

    [PostgresFact]
    public async Task TheFullPath_ApproveReceiveInspect_RestocksPartlyAndScrapsTheRest_WithAuditOutboxNoStore_AndTheBuyerWhitelist()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var support = await LoginAsync(Permissions.Roles.Support);
        var order = await OrderAsync(buyer.UserId);
        (await OnHandAsync(order.Variant1), await OnHandAsync(order.Variant2)).Should().Be((7, 9));
        var id = (await OpenAsync(buyer.UserId, order)).Id;

        var read = await DataAsync(await support.Client.GetAsync($"/api/v1/admin/returns/{id}"));
        var approve = await support.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/approve", new { rowVersion = Version(read) });
        (approve.Headers.CacheControl!.Private, approve.Headers.CacheControl.NoStore).Should().Be((true, true));
        var approved = await DataAsync(approve);
        (approved.GetProperty("status").GetString(), approved.GetProperty("decidedBy").GetGuid()).Should().Be(("approved", support.UserId));
        Version(approved).Should().NotBe(Version(read));

        var received = await DataAsync(await support.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/received", new { rowVersion = Version(approved) }));
        received.GetProperty("status").GetString().Should().Be("received");
        var lines = received.GetProperty("lines").EnumerateArray().ToDictionary(l => l.GetProperty("sku").GetString()!, l => l.GetProperty("id").GetGuid());
        var inspect = await support.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/inspect", new
        {
            rowVersion = Version(received),
            lines = new object[]
            {
                new { returnLineId = lines["SKU-A"], acceptedQty = 2, restockQty = 1, note = "  One coil burnt, one fine " },
                new { returnLineId = lines["SKU-B"], acceptedQty = 1, restockQty = 0, note = (string?)null }
            }
        });
        inspect.Headers.CacheControl!.NoStore.Should().BeTrue();
        var inspected = await DataAsync(inspect);
        (inspected.GetProperty("status").GetString(), inspected.GetProperty("restockPending").GetBoolean(), inspected.GetProperty("inspectedBy").GetGuid())
            .Should().Be(("inspected", false, support.UserId));
        inspected.GetProperty("lines").EnumerateArray()
            .Select(l => (l.GetProperty("sku").GetString(), l.GetProperty("acceptedQuantity").GetInt32(), l.GetProperty("restockQuantity").GetInt32()))
            .Should().Equal(("SKU-A", 2, 1), ("SKU-B", 1, 0));
        inspected.GetProperty("lines")[0].GetProperty("inspectionNote").GetString().Should().Be("One coil burnt, one fine");

        // One unit of A back where the order took it; B accepted but scrapped (no movement).
        (await OnHandAsync(order.Variant1), await OnHandAsync(order.Variant2)).Should().Be((8, 9));
        await using (var db = Db())
        {
            var moves = await db.StockMovements.Where(m => m.ReferenceId == id).ToListAsync();
            moves.Should().ContainSingle().Which.Should().Match<StockMovement>(m =>
                m.Delta == 1 && m.Reason == RestockReason.ReturnRestocked && m.ReferenceType == "return" && m.OrderId == order.Id && m.ActorId == support.UserId);
            (await db.AuditLogs.Where(a => a.EntityId == id.ToString() && a.Action.StartsWith("return.")).Select(a => a.Action).ToListAsync())
                .Should().BeEquivalentTo(["return.requested", "return.approved", "return.received", "return.inspected"]);
            (await db.AuditLogs.Where(a => a.EntityId == id.ToString() && a.Action == "return.inspected").Select(a => a.ActorId).SingleAsync()).Should().Be(support.UserId);
            (await db.OutboxMessages.Where(m => m.AggregateId == id).Select(m => m.EventType).ToListAsync()).Should().BeEquivalentTo(
                [nameof(ReturnRequestedEvent), nameof(ReturnApprovedEvent), nameof(ReturnReceivedEvent), nameof(ReturnInspectedEvent)]);
        }

        // The buyer sees the status and what was accepted: no staff ids, no notes, no restock figures, no rowVersion.
        var mine = await DataAsync(await buyer.Client.GetAsync($"/api/v1/returns/{id}"));
        mine.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["id", "orderId", "orderNumber", "companyId", "requestedBy", "status",
            "reasonCode", "note", "requestedAtUtc", "withdrawnAtUtc", "decidedAtUtc", "rejectionReason", "receivedAtUtc", "inspectedAtUtc", "lines"]);
        mine.GetProperty("lines")[0].EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo(["orderItemId", "variantId", "sku", "title", "quantity", "acceptedQuantity"]);
        (mine.GetProperty("status").GetString(), mine.GetProperty("lines")[0].GetProperty("acceptedQuantity").GetInt32()).Should().Be(("inspected", 2));
        var listed = (await DataAsync(await buyer.Client.GetAsync("/api/v1/returns"))).GetProperty("items")[0];
        listed.GetRawText().Should().NotContain(support.UserId.ToString()).And.NotContain("burnt").And.NotContain("restock");
        (await ErrorAsync(await buyer.Client.PostAsync($"/api/v1/returns/{id}/withdraw", null), HttpStatusCode.Conflict)).Should().Be("RETURN_NOT_WITHDRAWABLE");
    }

    [PostgresFact]
    public async Task TheBuyersOrderDetail_CarriesTheOrderLineId_ThatOpensAReturn_AndTheLineFieldsStayWhitelisted()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var order = await OrderAsync(buyer.UserId);
        var detail = await DataAsync(await buyer.Client.GetAsync($"/api/v1/orders/{order.Id}"));
        var items = detail.GetProperty("subOrders").EnumerateArray().SelectMany(s => s.GetProperty("items").EnumerateArray()).ToList();
        items.Select(i => i.GetProperty("id").GetGuid()).Should().BeEquivalentTo([order.Line1, order.Line2]);
        string[] allowed = ["id", "variantId", "sku", "title", "quantity", "unitPriceMinor", "discountMinor", "taxMinor", "totalMinor", "priceSource", "contractRef"];
        foreach (var item in items) item.EnumerateObject().Select(p => p.Name).Should().BeSubsetOf(allowed);

        using var request = new HttpRequestMessage(HttpMethod.Post, $"/api/v1/orders/{order.Number}/returns")
        {
            Content = JsonContent.Create(new { lines = new[] { new { orderItemId = items[0].GetProperty("id").GetGuid(), quantity = 1 } }, reasonCode = "damaged" })
        };
        request.Headers.Add("Idempotency-Key", "f24-" + Guid.NewGuid().ToString("N"));
        var created = await DataAsync(await buyer.Client.SendAsync(request), HttpStatusCode.Created);
        created.GetProperty("lines")[0].GetProperty("orderItemId").GetGuid().Should().Be(items[0].GetProperty("id").GetGuid());
    }

    [PostgresFact]
    public async Task Reject_NeedsAReason_FreesTheUnits_AndTheBuyerSeesTheReason()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var admin = await LoginAsync(Permissions.Roles.Admin);
        var order = await OrderAsync(buyer.UserId);
        var id = (await OpenAsync(buyer.UserId, order, line2: false)).Id;
        var version = Version(await DataAsync(await admin.Client.GetAsync($"/api/v1/admin/returns/{id}")));

        (await ErrorAsync(await admin.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/reject", new { rowVersion = version, reason = " no " }), HttpStatusCode.BadRequest))
            .Should().Be("VALIDATION_ERROR");
        (await ErrorAsync(await admin.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/reject", new { rowVersion = version, reason = new string('x', 501) }), HttpStatusCode.BadRequest))
            .Should().Be("VALIDATION_ERROR");
        var rejected = await DataAsync(await admin.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/reject",
            new { rowVersion = version, reason = "  Photos show installation damage " }));
        (rejected.GetProperty("status").GetString(), rejected.GetProperty("rejectionReason").GetString(), rejected.GetProperty("decidedBy").GetGuid())
            .Should().Be(("rejected", "Photos show installation damage", admin.UserId));
        var mine = await DataAsync(await buyer.Client.GetAsync($"/api/v1/returns/{id}"));
        (mine.GetProperty("status").GetString(), mine.GetProperty("rejectionReason").GetString()).Should().Be(("rejected", "Photos show installation damage"));

        (await OpenAsync(buyer.UserId, order, line2: false)).Status.Should().Be("requested", "a rejected return frees its units");
        await using var db = Db();
        (await db.AuditLogs.CountAsync(a => a.EntityId == id.ToString() && a.Action == "return.rejected")).Should().Be(1);
        (await db.OutboxMessages.CountAsync(m => m.AggregateId == id && m.EventType == nameof(ReturnRejectedEvent))).Should().Be(1);
    }

    [PostgresFact]
    public async Task EveryMoveFromTheWrongStatus_Is409WithTheCode_AStaleRowVersionIs409Changed_AndNothingIsWritten()
    {
        var buyer = await PlainUserAsync();
        var staff = await PlainUserAsync();
        var order = await OrderAsync(buyer, qty1: 5);
        var reason = new RejectReturnRequest(null, "Not covered by the policy");
        async Task<string> Refused(Guid id, Func<ReturnRequestService, uint, Task<StaffReturnDto>> move)
        {
            var version = (await StaffAsync(staff, id)).RowVersion;
            return (await FluentActions.Invoking(() => AsStaffAsync(staff, s => move(s, version))).Should().ThrowAsync<ConflictException>()).Which.Code;
        }
        // Requested: may not be received or inspected.
        var id = (await OpenAsync(buyer, order, qty1: 1, line2: false)).Id;
        (await Refused(id, (s, v) => s.MarkReceivedAsync(id, new ReturnStepRequest(v)))).Should().Be("RETURN_INVALID_TRANSITION");
        (await Refused(id, (s, v) => s.InspectAsync(id, new InspectReturnRequest(v, [new(Guid.NewGuid(), 1, 0, null)])))).Should().Be("RETURN_INVALID_TRANSITION");

        // Stale and missing rowVersion; unknown return.
        var stale = (await StaffAsync(staff, id)).RowVersion;
        await AsStaffAsync(staff, s => s.ApproveAsync(id, new ReturnStepRequest(stale)));
        (await FluentActions.Invoking(() => AsStaffAsync(staff, s => s.MarkReceivedAsync(id, new ReturnStepRequest(stale))))
            .Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("RETURN_CHANGED");
        await FluentActions.Invoking(() => AsStaffAsync(staff, s => s.MarkReceivedAsync(id, new ReturnStepRequest(null)))).Should().ThrowAsync<ValidationException>();
        await FluentActions.Invoking(() => AsStaffAsync(staff, s => s.ApproveAsync(Guid.NewGuid(), new ReturnStepRequest(1)))).Should().ThrowAsync<KeyNotFoundException>();

        // Approved: no second approval, no rejection, no inspection before receipt, no withdrawal.
        (await Refused(id, (s, v) => s.ApproveAsync(id, new ReturnStepRequest(v)))).Should().Be("RETURN_INVALID_TRANSITION");
        (await Refused(id, (s, v) => s.RejectAsync(id, reason with { RowVersion = v }))).Should().Be("RETURN_INVALID_TRANSITION");
        (await Refused(id, (s, v) => s.InspectAsync(id, new InspectReturnRequest(v, [new(Guid.NewGuid(), 1, 0, null)])))).Should().Be("RETURN_INVALID_TRANSITION");
        await using (var db = Db())
            (await FluentActions.Invoking(() => Service(db, buyer).WithdrawAsync(buyer, id)).Should().ThrowAsync<ConflictException>())
                .Which.Code.Should().Be("RETURN_NOT_WITHDRAWABLE");

        // Received: not received again, not approved. Inspected: nothing more until F2.3b.
        var v1 = (await StaffAsync(staff, id)).RowVersion;
        var rec = await AsStaffAsync(staff, s => s.MarkReceivedAsync(id, new ReturnStepRequest(v1)));
        (await Refused(id, (s, v) => s.MarkReceivedAsync(id, new ReturnStepRequest(v)))).Should().Be("RETURN_INVALID_TRANSITION");
        (await Refused(id, (s, v) => s.ApproveAsync(id, new ReturnStepRequest(v)))).Should().Be("RETURN_INVALID_TRANSITION");
        await AsStaffAsync(staff, s => s.InspectAsync(id, new InspectReturnRequest(rec.RowVersion, [new(rec.Lines[0].Id, 1, 1, null)])));
        (await Refused(id, (s, v) => s.InspectAsync(id, new InspectReturnRequest(v, [new(rec.Lines[0].Id, 1, 1, null)])))).Should().Be("RETURN_INVALID_TRANSITION");
        (await Refused(id, (s, v) => s.RejectAsync(id, reason with { RowVersion = v }))).Should().Be("RETURN_INVALID_TRANSITION");

        // Rejected and withdrawn ones never move again.
        var rejected = (await OpenAsync(buyer, order, qty1: 1, line2: false)).Id;
        await AsStaffAsync(staff, async s => await s.RejectAsync(rejected, reason with { RowVersion = (await StaffAsync(staff, rejected)).RowVersion }));
        var withdrawn = (await OpenAsync(buyer, order, qty1: 1, line2: false)).Id;
        await using (var db = Db()) await Service(db, buyer).WithdrawAsync(buyer, withdrawn);
        foreach (var closed in new[] { rejected, withdrawn })
        {
            (await Refused(closed, (s, v) => s.ApproveAsync(closed, new ReturnStepRequest(v)))).Should().Be("RETURN_INVALID_TRANSITION");
            (await Refused(closed, (s, v) => s.RejectAsync(closed, reason with { RowVersion = v }))).Should().Be("RETURN_INVALID_TRANSITION");
            (await Refused(closed, (s, v) => s.MarkReceivedAsync(closed, new ReturnStepRequest(v)))).Should().Be("RETURN_INVALID_TRANSITION");
        }

        await using var check = Db();
        (await check.AuditLogs.Where(a => a.EntityId == id.ToString() && a.Action.StartsWith("return.")).Select(a => a.Action).ToListAsync())
            .Should().BeEquivalentTo(["return.requested", "return.approved", "return.received", "return.inspected"], "a refused move writes nothing");
    }

    [PostgresFact]
    public async Task TheMoves_NeedReturnsManage_SupportAndAdmin_OthersForbidden_AnonymousUnauthorized()
    {
        var buyer = await LoginAsync(Permissions.Roles.Buyer);
        var order = await OrderAsync(buyer.UserId);
        var id = (await OpenAsync(buyer.UserId, order, qty1: 1, line2: false)).Id;
        var body = new { rowVersion = 1, reason = "Not covered by policy", lines = Array.Empty<object>() };
        string[] moves = ["approve", "reject", "received", "inspect"];

        using var anonymous = factory.CreateClient();
        foreach (var move in moves)
            (await anonymous.PostAsJsonAsync($"/api/v1/admin/returns/{id}/{move}", body)).StatusCode.Should().Be(HttpStatusCode.Unauthorized, move);
        foreach (var role in new[] { Permissions.Roles.Buyer, Permissions.Roles.Seller, Permissions.Roles.Moderator, Permissions.Roles.Finance })
        {
            var who = role == Permissions.Roles.Buyer ? buyer : await LoginAsync(role);
            foreach (var move in moves)
                (await who.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/{move}", body)).StatusCode.Should().Be(HttpStatusCode.Forbidden, $"{role} {move}");
        }
        foreach (var role in new[] { Permissions.Roles.Support, Permissions.Roles.Admin })
        {
            var staff = await LoginAsync(role);
            (await ErrorAsync(await staff.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/received",
                new { rowVersion = Version(await DataAsync(await staff.Client.GetAsync($"/api/v1/admin/returns/{id}"))) }), HttpStatusCode.Conflict))
                .Should().Be("RETURN_INVALID_TRANSITION", role);
            (await ErrorAsync(await staff.Client.PostAsJsonAsync($"/api/v1/admin/returns/{id}/approve", new { rowVersion = 1 }), HttpStatusCode.Conflict))
                .Should().Be("RETURN_CHANGED", role);
        }
        await using var db = Db();
        (await db.ReturnRequests.Where(r => r.Id == id).Select(r => r.Status).SingleAsync()).Should().Be(ReturnStatus.Requested);
    }

    [PostgresFact]
    public async Task InspectionQuantities_AcceptedAtMostAsked_RestockedAtMostAccepted_EveryLineOnce()
    {
        var buyer = await PlainUserAsync();
        var staff = await PlainUserAsync();
        var order = await OrderAsync(buyer);
        var id = (await OpenAsync(buyer, order, qty1: 2)).Id;
        await AsStaffAsync(staff, async s => await s.ApproveAsync(id, new ReturnStepRequest((await StaffAsync(staff, id)).RowVersion)));
        var received = await AsStaffAsync(staff, async s => await s.MarkReceivedAsync(id, new ReturnStepRequest((await StaffAsync(staff, id)).RowVersion)));
        var (a, b) = (received.Lines.Single(l => l.Sku == "SKU-A").Id, received.Lines.Single(l => l.Sku == "SKU-B").Id);

        async Task<string> Field(params ReturnInspectionInput[] lines) =>
            (await FluentActions.Invoking(() => AsStaffAsync(staff, s => s.InspectAsync(id, new InspectReturnRequest(received.RowVersion, lines))))
                .Should().ThrowAsync<ValidationException>()).Which.Errors.Single().Field;
        (await Field(new(a, 3, 0, null), new(b, 1, 0, null))).Should().Be("lines[0].acceptedQty", "2 were asked");
        (await Field(new(a, 2, 3, null), new(b, 1, 0, null))).Should().Be("lines[0].restockQty");
        (await Field(new(a, 1, 2, null), new(b, 1, 0, null))).Should().Be("lines[0].restockQty");
        (await Field(new(a, -1, 0, null), new(b, 1, 0, null))).Should().Be("lines[0].acceptedQty");
        (await Field(new(a, 2, -1, null), new(b, 1, 0, null))).Should().Be("lines[0].restockQty");
        (await Field(new(a, 2, 0, null), new(b, 2, 0, null))).Should().Be("lines[1].acceptedQty");
        (await Field(new ReturnInspectionInput(a, 2, 0, null))).Should().Be("lines", "every line once");
        (await Field(new(a, 2, 0, null), new(a, 1, 0, null))).Should().Be("lines");
        (await Field(new(a, 2, 0, null), new(Guid.NewGuid(), 1, 0, null))).Should().Be("lines", "a line of another return");
        (await Field(new(a, 2, 0, new string('n', 501)), new(b, 1, 0, null))).Should().Be("lines[0].note");
        (await Field()).Should().Be("lines");

        // Accepted nothing and restocked nothing is a valid inspection: nothing goes back on the shelf, nothing is pending.
        var done = await AsStaffAsync(staff, s => s.InspectAsync(id, new InspectReturnRequest(received.RowVersion, [new(a, 0, 0, " "), new(b, 1, 0, null)])));
        (done.Status, done.RestockPending, done.Lines.Single(l => l.Id == a).AcceptedQuantity, done.Lines.Single(l => l.Id == a).InspectionNote)
            .Should().Be(("inspected", false, 0, null));
        (await OnHandAsync(order.Variant1), await OnHandAsync(order.Variant2)).Should().Be((7, 9));
    }

    [PostgresFact]
    public async Task TwoPartialReturnsOfOneVariant_EachRestock_AndNothingEverExceedsWhatTheOrderDeducted()
    {
        var buyer = await PlainUserAsync();
        var staff = await PlainUserAsync();
        async Task<StaffReturnDto> InspectAllAsync(Guid id, int accepted, int restock)
        {
            await AsStaffAsync(staff, async s => await s.ApproveAsync(id, new ReturnStepRequest((await StaffAsync(staff, id)).RowVersion)));
            var r = await AsStaffAsync(staff, async s => await s.MarkReceivedAsync(id, new ReturnStepRequest((await StaffAsync(staff, id)).RowVersion)));
            return await AsStaffAsync(staff, s => s.InspectAsync(id, new InspectReturnRequest(r.RowVersion, [new(r.Lines[0].Id, accepted, restock, null)])));
        }

        var order = await OrderAsync(buyer);   // line 1: 3 delivered and deducted: on hand 7
        (await InspectAllAsync((await OpenAsync(buyer, order, qty1: 2, line2: false)).Id, 2, 2)).RestockPending.Should().BeFalse();
        (await InspectAllAsync((await OpenAsync(buyer, order, qty1: 1, line2: false)).Id, 1, 1)).RestockPending.Should().BeFalse();
        (await OnHandAsync(order.Variant1)).Should().Be(10, "two returns of one variant each got their own key");
        await using (var db = Db())
            (await FluentActions.Invoking(() => Inventory(db).RestockAsync(new RestockRequest(order.Variant1, 1, RestockReason.ReturnRestocked, "return", Guid.NewGuid(),
                AgainstOrderId: order.Id))).Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("RESTOCK_EXCEEDS_DEDUCTED");

        // An order whose holds took only 1 of 3: restocking 3 is refused by the order's cap, the inspection stands, the restock stays pending.
        var short1 = await OrderAsync(buyer, qty1: 3, deducted1: 1);
        var pending = await InspectAllAsync((await OpenAsync(buyer, short1, line2: false)).Id, 3, 3);
        (pending.Status, pending.RestockPending).Should().Be(("inspected", true));
        (await OnHandAsync(short1.Variant1)).Should().Be(9, "nothing put back beyond the deduction");
        await using (var db = Db())
        {
            (await db.StockMovements.CountAsync(m => m.ReferenceId == pending.Id)).Should().Be(0);
            await FluentActions.Invoking(() => Inventory(db).RestockAsync(new RestockRequest(order.Variant1, 1, RestockReason.ReturnRestocked, "order", order.Id,
                AgainstOrderId: Guid.NewGuid()))).Should().ThrowAsync<ValidationException>();
        }
    }

    [PostgresFact]
    public async Task ApproveAndWithdraw_InParallel_ExactlyOneWins_FourRounds()
    {
        var buyer = await PlainUserAsync();
        var staff = await PlainUserAsync();
        for (var round = 0; round < 4; round++)
        {
            var order = await OrderAsync(buyer);
            var id = (await OpenAsync(buyer, order, qty1: 1, line2: false)).Id;
            var version = (await StaffAsync(staff, id)).RowVersion;
            using var start = new SemaphoreSlim(0);
            async Task<string> Run(Func<ReturnRequestService, Task> act, Guid who)
            {
                await using var db = Db();
                var service = Service(db, who);
                await start.WaitAsync();
                try
                {
                    await act(service);
                    return "ok";
                }
                catch (ConflictException e)
                {
                    return e.Code;
                }
            }
            var tasks = new List<Task<string>>();
            for (var i = 0; i < 3; i++)
            {
                tasks.Add(Run(s => s.ApproveAsync(id, new ReturnStepRequest(version)), staff));
                tasks.Add(Run(s => s.WithdrawAsync(buyer, id), buyer));
            }
            start.Release(6);
            var results = await Task.WhenAll(tasks);
            await using var db = Db();
            var status = await db.ReturnRequests.Where(r => r.Id == id).Select(r => r.Status).SingleAsync();
            status.Should().BeOneOf(ReturnStatus.Approved, ReturnStatus.Withdrawn);
            var approvals = results.Where((_, i) => i % 2 == 0).ToList();
            if (status == ReturnStatus.Approved)
            {
                approvals.Count(r => r == "ok").Should().Be(1, $"round {round}: {string.Join(",", results)}");
                results.Where((_, i) => i % 2 == 1).Should().OnlyContain(r => r == "RETURN_NOT_WITHDRAWABLE");
            }
            else approvals.Should().OnlyContain(r => r == "RETURN_CHANGED", $"round {round}");
            (await db.AuditLogs.CountAsync(a => a.EntityId == id.ToString() && (a.Action == "return.approved" || a.Action == "return.withdrawn"))).Should().Be(1);
        }
    }

    [PostgresFact]
    public async Task ARestockThatDidNotRunAfterTheInspection_IsFinishedByTheStockSweep_Once()
    {
        var buyer = await PlainUserAsync();
        var staff = await PlainUserAsync();
        var order = await OrderAsync(buyer);
        var id = (await OpenAsync(buyer, order, qty1: 2)).Id;
        await AsStaffAsync(staff, async s => await s.ApproveAsync(id, new ReturnStepRequest((await StaffAsync(staff, id)).RowVersion)));
        var r = await AsStaffAsync(staff, async s => await s.MarkReceivedAsync(id, new ReturnStepRequest((await StaffAsync(staff, id)).RowVersion)));
        StaffReturnDto crashed;
        await using (var db = Db())
            crashed = await Service(db, staff, new RestockFails(Inventory(db))).InspectAsync(id, new InspectReturnRequest(r.RowVersion,
                [new(r.Lines.Single(l => l.Sku == "SKU-A").Id, 2, 2, null), new(r.Lines.Single(l => l.Sku == "SKU-B").Id, 1, 1, null)]));
        (crashed.Status, crashed.RestockPending).Should().Be(("inspected", true));
        (await StaffAsync(staff, id)).RestockPending.Should().BeTrue("a staff read shows it too");
        (await OnHandAsync(order.Variant1), await OnHandAsync(order.Variant2)).Should().Be((7, 9));

        async Task<StockReconciliationPass> SweepAsync(DateTime at)
        {
            await using var db = Db();
            return await new StockReconciliationService(db, Inventory(db), new StockReconciliationOptions(), TimeProvider.System).RunOnceAsync(at);
        }
        await SweepAsync(DateTime.UtcNow.AddMinutes(2));   // inside the grace period: left alone
        (await OnHandAsync(order.Variant1)).Should().Be(7);
        await SweepAsync(DateTime.UtcNow.AddMinutes(10));
        await SweepAsync(DateTime.UtcNow.AddMinutes(10));
        (await OnHandAsync(order.Variant1), await OnHandAsync(order.Variant2)).Should().Be((9, 10), "put back once, against the order");
        (await StaffAsync(staff, id)).RestockPending.Should().BeFalse();
        await using var check = Db();
        (await check.StockMovements.Where(m => m.ReferenceId == id).Select(m => new { m.Delta, m.ActorId, m.OrderId }).ToListAsync())
            .Should().BeEquivalentTo([new { Delta = 2, ActorId = (Guid?)null, OrderId = (Guid?)order.Id }, new { Delta = 1, ActorId = (Guid?)null, OrderId = (Guid?)order.Id }]);
        var audit = await check.AuditLogs.Where(a => a.Action == StockReconciliationService.AuditAction && a.EntityId == id.ToString()).ToListAsync();
        audit.Should().ContainSingle().Which.EntityName.Should().Be("return_requests");
        audit[0].NewValuesJson.Should().Contain("return_restock");
    }

    [PostgresFact]
    public async Task TheDatabase_EnforcesTheStatusGraph_InspectOnce_AndTheInspectionLimits()
    {
        var buyer = await PlainUserAsync();
        var staff = await PlainUserAsync();
        var order = await OrderAsync(buyer);
        var id = (await OpenAsync(buyer, order, qty1: 2, line2: false)).Id;
        await using var db = Db();

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

        var now = DateTime.UtcNow;
        (await Sql($"UPDATE return_requests SET status = 'Received', decided_by = {staff}, decided_at = {now}, received_by = {staff}, received_at = {now} WHERE id = {id}"))
            .Should().Be(PostgresErrorCodes.CheckViolation, "Requested → Received skips the decision");
        (await Sql($"UPDATE return_request_lines SET accepted_quantity = 1, restock_quantity = 0 WHERE return_request_id = {id}"))
            .Should().Be(PostgresErrorCodes.CheckViolation, "only a received return is inspected");
        (await Sql($"UPDATE return_requests SET status = 'Approved' WHERE id = {id}")).Should().Be(PostgresErrorCodes.CheckViolation, "a decision names who and when");
        (await Sql($"UPDATE return_requests SET status = 'Approved', decided_by = {staff}, decided_at = {now} WHERE id = {id}")).Should().BeNull();
        (await Sql($"UPDATE return_requests SET status = 'Received', received_by = {staff}, received_at = {now} WHERE id = {id}")).Should().BeNull();
        (await Sql($"UPDATE return_requests SET status = 'Approved', received_by = NULL, received_at = NULL WHERE id = {id}")).Should().Be(PostgresErrorCodes.CheckViolation);
        (await Sql($"UPDATE return_request_lines SET accepted_quantity = 3, restock_quantity = 0 WHERE return_request_id = {id}")).Should().Be(PostgresErrorCodes.CheckViolation, "2 asked");
        (await Sql($"UPDATE return_request_lines SET accepted_quantity = 1, restock_quantity = 2 WHERE return_request_id = {id}")).Should().Be(PostgresErrorCodes.CheckViolation);
        (await Sql($"UPDATE return_requests SET status = 'Inspected', inspected_by = {staff}, inspected_at = {now} WHERE id = {id}"))
            .Should().Be(PostgresErrorCodes.CheckViolation, "an inspected return has every line inspected (checked at commit)");
        (await Sql($"UPDATE return_request_lines SET accepted_quantity = 2, restock_quantity = 1 WHERE return_request_id = {id}"))
            .Should().Be(PostgresErrorCodes.CheckViolation, "only an inspected return has inspected lines (checked at commit)");

        await using (var tx = await db.Database.BeginTransactionAsync())
        {
            (await Sql($"UPDATE return_request_lines SET accepted_quantity = 2, restock_quantity = 1 WHERE return_request_id = {id}")).Should().BeNull();
            (await Sql($"UPDATE return_requests SET status = 'Inspected', inspected_by = {staff}, inspected_at = {now} WHERE id = {id}")).Should().BeNull();
            await tx.CommitAsync();
        }
        (await Sql($"UPDATE return_request_lines SET restock_quantity = 0 WHERE return_request_id = {id}")).Should().Be(PostgresErrorCodes.CheckViolation, "inspected once");
        (await Sql($"UPDATE return_request_lines SET quantity = 1 WHERE return_request_id = {id}")).Should().Be(PostgresErrorCodes.CheckViolation);
        (await Sql($"UPDATE return_requests SET status = 'Requested' WHERE id = {id}")).Should().Be(PostgresErrorCodes.CheckViolation);
        (await Sql($"INSERT INTO return_request_lines (id, return_request_id, order_item_id, quantity, created_at) VALUES ({Guid.NewGuid()}, {id}, {order.Line2}, 1, now())"))
            .Should().Be(PostgresErrorCodes.CheckViolation, "lines are added only to a new request");
    }
}
