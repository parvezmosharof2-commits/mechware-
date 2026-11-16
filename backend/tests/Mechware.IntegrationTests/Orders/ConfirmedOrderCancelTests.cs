using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Companies;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Application.Inventory;
using Mechware.Application.Orders;
using Mechware.Application.Payments;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Credit;
using Mechware.Domain.Inventory;
using Mechware.Domain.Orders;
using Mechware.Domain.Payments;
using Mechware.Domain.Pricing;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Payments;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// E7.2 against a real database: a confirmed order cancelled by staff ends cancelled, with its deducted stock back in the warehouse,
/// its credit charge reduced in the same transaction (CS19), its captured gateway money in the refund queue, and it never deadlocks
/// with verifications, bounces, payment webhooks or new orders (CS16). Lock order: gateway payments → orders (FOR NO KEY UPDATE) →
/// companies → promotions.
/// </summary>
public class ConfirmedOrderCancelTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private const string Ip = "203.0.113.72";
    private const long Price = 100_000;
    private static long _binSeq = 3_900_000_000;
    private static DateOnly Today => CreditCalendar.DhakaDate(DateTime.UtcNow);
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");
    private static string Ref() => "TRX" + Guid.NewGuid().ToString("N")[..12].ToUpperInvariant();
    private readonly MockPaymentProvider _mock = new();

    private sealed class Staff(Guid id, string correlationId = "corr-cancel") : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => Ip;
        public string? UserAgent => null;
        public string? CorrelationId => correlationId;
        public bool HasPermission(string permission) => true;
    }

    /// <summary>Stock returns fail the first <paramref name="failures"/> times (a crash after the cancel committed).</summary>
    private sealed class FlakyRestock(IInventoryService inner, int failures) : IInventoryService
    {
        private int _left = failures;
        public Task<RestockResult> RestockAsync(RestockRequest request, CancellationToken ct = default) =>
            Interlocked.Decrement(ref _left) >= 0 ? throw new TimeoutException("the database went away") : inner.RestockAsync(request, ct);
        public Task<ReserveStockResult> ReserveAsync(ReserveStockRequest request, CancellationToken ct = default) => inner.ReserveAsync(request, ct);
        public Task<int> CommitAsync(Guid referenceId, CancellationToken ct = default) => inner.CommitAsync(referenceId, ct);
        public Task<int> CancelAsync(Guid referenceId, CancellationToken ct = default) => inner.CancelAsync(referenceId, ct);
        public Task<int> ReleaseExpiredAsync(DateTime nowUtc, int batchSize = 500, CancellationToken ct = default) => inner.ReleaseExpiredAsync(nowUtc, batchSize, ct);
    }

    private Task<Guid> UserAsync() => TestUsers.CreateAsync(fixture);

    private async Task<CancelledOrderDto> CancelAsync(Guid order, Guid staff, string reason = "customer called: wrong part ordered",
        Func<IInventoryService, IInventoryService>? wrap = null, string correlationId = "corr-cancel")
    {
        await using var db = fixture.CreateContext();
        return await CancelInAsync(db, order, staff, reason, wrap, correlationId);
    }

    private static async Task<CancelledOrderDto> CancelInAsync(MechwareDbContext db, Guid order, Guid staff, string reason = "customer called: wrong part ordered",
        Func<IInventoryService, IInventoryService>? wrap = null, string correlationId = "corr-cancel")
    {
        var user = new Staff(staff, correlationId);
        var inventory = new InventoryService(db, new NoopLockProvider(), TimeProvider.System, user: user);
        var service = new OrderCancellationService(db, wrap is null ? inventory : wrap(inventory), new CreditLedgerService(db, user, TimeProvider.System),
            user, TimeProvider.System);
        return await service.CancelConfirmedAsync(order, new CancelConfirmedOrderRequest(reason));
    }

    private OrderService Orders(MechwareDbContext db) => new(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
        ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db));

    private async Task<SeededVariant> VariantAsync(int onHand = 10, long price = Price)
    {
        await using var db = fixture.CreateContext();
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: price);
    }

    private async Task<(int OnHand, int Reserved)> StockAsync(SeededVariant v)
    {
        await using var db = fixture.CreateContext();
        var item = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == v.InventoryItemId);
        return (item.OnHand, item.Reserved);
    }

    private sealed record Team(Guid Company, Guid Admin, IReadOnlyList<Guid> Buyers);

    private async Task<Team> TeamAsync(long limitMinor = 100_000_000, int buyers = 1)
    {
        var admin = await UserAsync();
        var buyerIds = new List<Guid>();
        for (var i = 0; i < buyers; i++) buyerIds.Add(await UserAsync());
        await using var db = fixture.CreateContext();
        var companies = new CompanyService(db);
        var company = (await companies.RegisterAsync(admin, new RegisterCompanyRequest("Cancelled Gears Ltd", "TL-E72-1",
            (Interlocked.Increment(ref _binSeq) + 1_000_000_000_000L).ToString("D13"), null, null))).Id;
        foreach (var buyer in buyerIds)
            await companies.AddAcceptedMemberAsync(admin, company, new AddMemberRequest(buyer, CompanyRoles.Buyer, null, null));
        await companies.AdminActivateAsync(company);
        await companies.AdminSetCreditAsync(company, new SetCreditRequest(CreditTerms.Net30, limitMinor));
        return new Team(company, admin, buyerIds);
    }

    /// <summary>A credit order: confirmed at once, charged to the company and its stock committed (deducted).</summary>
    private async Task<PlacedOrder> CreditOrderAsync(Team t, Guid buyer, params (Guid Variant, int Quantity)[] lines)
    {
        await using var db = fixture.CreateContext();
        var placed = await Orders(db).PlaceAsync(new PlaceOrderRequest(buyer, Key(), lines.Select(l => new PlaceOrderLine(l.Variant, l.Quantity)).ToList(),
            null, t.Company, PayOnCredit: true));
        placed.Status.Should().Be("confirmed");
        return placed;
    }

    /// <summary>A personal order paid through the mock gateway: confirmed, its payment captured and its stock committed.</summary>
    private async Task<(PlacedOrder Order, Guid PaymentId, string ProviderRef)> PaidOrderAsync(Guid buyer, Guid variant, params string[] coupons)
    {
        await using var db = fixture.CreateContext();
        var inv = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
        var placed = await Orders(db).PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(variant, 1)], CouponCodes: coupons));
        var payments = new PaymentService(db, [_mock], inv, TimeProvider.System, ShippingTestKit.Deadlines(db));
        var session = await payments.StartAsync(buyer, placed.OrderId, "mock");
        var providerRef = session.RedirectUrl[(session.RedirectUrl.LastIndexOf('/') + 1)..];
        _mock.SimulatePayment(providerRef);
        (await WebhookAsync(payments, providerRef)).Should().Be(WebhookOutcome.Confirmed);
        return (placed, session.PaymentId, providerRef);
    }

    private static Task<WebhookOutcome> WebhookAsync(PaymentService payments, string providerRef)
    {
        var body = $$"""{ "id": "evt_{{Guid.NewGuid():N}}", "providerRef": "{{providerRef}}" }""";
        var ts = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return payments.HandleWebhookAsync("mock", WebhookVerifier.Sign(MockPaymentProvider.DefaultSecret, ts, body), ts.ToString(), body);
    }

    private async Task<(Company Company, List<CreditLedgerEntry> Lines)> CreditStateAsync(Guid company)
    {
        await using var db = fixture.CreateContext();
        return (await db.Companies.AsNoTracking().SingleAsync(c => c.Id == company),
            await db.CreditLedgerEntries.AsNoTracking().Where(l => l.CompanyId == company).OrderBy(l => l.Seq).ToListAsync());
    }

    /// <summary>The chain and the cached totals agree, and no order owes less than nothing.</summary>
    private async Task<List<CreditLedgerEntry>> LedgerAddsUpAsync(Guid companyId)
    {
        var (company, lines) = await CreditStateAsync(companyId);
        lines.Select(l => l.Seq).Should().Equal(Enumerable.Range(1, lines.Count).Select(i => (long)i));
        company.CreditUsedMinor.Should().Be(lines.Sum(l => l.ReceivableDeltaMinor));
        company.CreditOnAccountMinor.Should().Be(lines.Sum(l => l.OnAccountDeltaMinor));
        var ledger = new CreditLedger(company, lines);
        foreach (var order in lines.Where(l => l.OrderId is not null).Select(l => l.OrderId!.Value).Distinct())
        {
            var b = ledger.BalanceOf(order);
            (b.ReceivableMinor >= 0 && b.PaidMinor >= 0 && b.ReducedMinor <= b.ChargeMinor).Should().BeTrue($"order {order}: {b}");
        }
        return lines;
    }

    private async Task<Order> OrderAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.Orders.AsNoTracking().Include(o => o.SubOrders).Include(o => o.Events).SingleAsync(o => o.Id == id);
    }

    private async Task<T> CreditPayments<T>(Guid actor, Func<CreditPaymentService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(new CreditPaymentService(db, new Staff(actor, "corr-credit"), new CreditOptions(), TimeProvider.System));
    }

    private async Task<Guid> RecordedAsync(Team t, long amount, string method = "rtgs") =>
        (await CreditPayments(await UserAsync(), s => s.RecordAsync(t.Company, new RecordCreditPaymentRequest(method, amount, Today, Ref()), Key()))).Payment.Id;

    private async Task<Guid> VerifiedAsync(Team t, long amount, string method = "rtgs")
    {
        var payment = await RecordedAsync(t, amount, method);
        await CreditPayments(await UserAsync(), s => s.VerifyAsync(payment, new VerifyCreditPaymentRequest(Today)));
        return payment;
    }

    // ---- the whole cancel ---------------------------------------------------------------------------------------------

    [PostgresFact]
    public async Task CancellingAConfirmedCreditOrder_RestocksReleasesItsCreditAndRecordsEverything()
    {
        var t = await TeamAsync();
        var (bearing, belt) = (await VariantAsync(onHand: 10), await VariantAsync(onHand: 4, price: 25_000));
        var other = await CreditOrderAsync(t, t.Buyers[0], (bearing.VariantId, 1));
        var placed = await CreditOrderAsync(t, t.Buyers[0], (bearing.VariantId, 3), (belt.VariantId, 2));
        (await StockAsync(bearing), await StockAsync(belt)).Should().Be(((6, 0), (2, 0)), "a credit order deducts its stock at once");
        var usedBefore = (await CreditStateAsync(t.Company)).Company.CreditUsedMinor;
        var staff = await UserAsync();

        var result = await CancelAsync(placed.OrderId, staff);

        result.Should().BeEquivalentTo(new CancelledOrderDto(placed.OrderId, placed.OrderNumber, "cancelled", false, placed.GrandTotalMinor, 0, [],
            [new ReturnedStockDto(bearing.VariantId, 3), new ReturnedStockDto(belt.VariantId, 2)], false));
        (await StockAsync(bearing), await StockAsync(belt)).Should().Be(((9, 0), (4, 0)), "only on-hand grows; the other order keeps its unit");

        var order = await OrderAsync(placed.OrderId);
        order.Status.Should().Be(OrderStatus.Cancelled);
        order.SubOrders.Should().OnlyContain(s => s.Status == OrderStatus.Cancelled);
        order.Events.OrderBy(e => e.Sequence).Last().Should().BeEquivalentTo(new
        {
            FromStatus = OrderStatus.Confirmed,
            ToStatus = OrderStatus.Cancelled,
            ActorId = (Guid?)staff,
            Reason = "customer called: wrong part ordered"
        });

        var (company, lines) = (await CreditStateAsync(t.Company));
        company.CreditUsedMinor.Should().Be(usedBefore - placed.GrandTotalMinor);
        lines.Where(l => l.OrderId == placed.OrderId).Sum(l => l.ReceivableDeltaMinor).Should().Be(0);
        lines.Single(l => l.OrderId == placed.OrderId && l.Kind == CreditLedgerKind.ChargeReduced).Should().BeEquivalentTo(new
        {
            ReceivableDeltaMinor = -placed.GrandTotalMinor,
            ActorUserId = (Guid?)staff,
            CorrelationId = "corr-cancel",
            IdempotencyKey = $"reduce:cancel:{placed.OrderId:N}:receivable",
            Reason = "customer called: wrong part ordered"
        });
        lines.Where(l => l.OrderId == other.OrderId).Sum(l => l.ReceivableDeltaMinor).Should().Be(other.GrandTotalMinor, "the other order still owes");
        await LedgerAddsUpAsync(t.Company);

        await using var db = fixture.CreateContext();
        var movements = await db.StockMovements.AsNoTracking().Where(m => m.ReferenceId == placed.OrderId).ToListAsync();
        movements.Should().HaveCount(2).And.OnlyContain(m => m.Reason == RestockReason.OrderCancelled && m.ReferenceType == "order");
        movements.Sum(m => m.Delta).Should().Be(5);
        var audit = await db.AuditLogs.AsNoTracking().SingleAsync(a => a.Action == OrderCancellationService.CancelledAudit && a.EntityId == placed.OrderId.ToString());
        audit.Should().BeEquivalentTo(new { ActorId = (Guid?)staff, ActorIp = Ip, CorrelationId = "corr-cancel", EntityName = "orders" });
        using (var after = JsonDocument.Parse(audit.NewValuesJson!))
        {
            after.RootElement.GetProperty("creditReleasedMinor").GetInt64().Should().Be(placed.GrandTotalMinor);
            after.RootElement.GetProperty("reason").GetString().Should().Be("customer called: wrong part ordered");
        }
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "credit.charge_reduced" && a.EntityId == placed.OrderId.ToString())).Should().Be(1);
        (await db.AuditLogs.AsNoTracking().CountAsync(a => a.Action == "inventory.restocked" && a.ActorId == staff)).Should().Be(2);
        var outbox = await db.OutboxMessages.AsNoTracking().SingleAsync(m => m.AggregateId == placed.OrderId && m.EventType == nameof(ConfirmedOrderCancelledEvent));
        using (var payload = JsonDocument.Parse(outbox.Payload))
            (payload.RootElement.GetProperty("fromStatus").GetString(), payload.RootElement.GetProperty("companyId").GetGuid()).Should().Be(("Confirmed", t.Company));
    }

    [PostgresFact]
    public async Task CancellingAPaidGatewayOrder_PutsItsPaymentInTheRefundQueue_AndGivesThePromotionUseBack()
    {
        var v = await VariantAsync(onHand: 5);
        var buyer = await UserAsync();
        var code = "E72" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        var promotion = PromotionDefinition.Create(Guid.NewGuid(), "Promo " + code, code, PromotionKind.PercentOff,
            new PromotionScope(PromotionScopeKind.Variant, v.VariantId), percent: 10, globalLimit: 1, perUserLimit: 1);
        await using (var db = fixture.CreateContext())
        {
            db.Promotions.Add(promotion);
            await db.SaveChangesAsync();
        }
        var (placed, payment, _) = await PaidOrderAsync(buyer, v.VariantId, code);
        (await StockAsync(v)).Should().Be((4, 0));

        var result = await CancelAsync(placed.OrderId, await UserAsync());

        result.Should().BeEquivalentTo(new
        {
            Status = "cancelled",
            AlreadyCancelled = false,
            CreditReleasedMinor = 0L,
            RefundPaymentIds = new[] { payment },
            StockReturned = new[] { new ReturnedStockDto(v.VariantId, 1) },
            StockReturnPending = false
        });
        (await StockAsync(v)).Should().Be((5, 0));
        await using (var db = fixture.CreateContext())
        {
            (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment)).Should().BeEquivalentTo(new
            {
                Status = PaymentStatus.NeedsRefund,
                Note = "Order cancelled after payment: customer called: wrong part ordered"
            });
            (await db.Promotions.AsNoTracking().SingleAsync(p => p.Id == promotion.Id)).RedeemedCount.Should().Be(0);
            (await db.PromotionUserUsage.AsNoTracking().Where(u => u.PromotionId == promotion.Id).SumAsync(u => u.Uses)).Should().Be(0);
            (await db.PromotionRedemptions.CountAsync(r => r.OrderId == placed.OrderId)).Should().Be(0);
            (await db.CreditLedgerEntries.CountAsync(l => l.OrderId == placed.OrderId)).Should().Be(0, "not a credit order");
            var queue = await new RefundService(db, [_mock], TimeProvider.System).QueueAsync();
            queue.Should().ContainSingle(q => q.PaymentId == payment).Which.OrderId.Should().Be(placed.OrderId);
        }
    }

    [PostgresFact]
    public async Task AFiveHundredCharacterReason_StillFitsThePaymentNote()
    {
        var v = await VariantAsync(onHand: 2);
        var (placed, payment, _) = await PaidOrderAsync(await UserAsync(), v.VariantId);
        var reason = new string('r', Order.CancelReasonMaxLength);

        (await CancelAsync(placed.OrderId, await UserAsync(), reason)).RefundPaymentIds.Should().Equal(payment);

        await using var db = fixture.CreateContext();
        var note = (await db.Payments.AsNoTracking().SingleAsync(p => p.Id == payment)).Note!;
        (note.Length, note.StartsWith("Order cancelled after payment: rrr", StringComparison.Ordinal)).Should().Be((500, true));
        (await db.OrderEvents.AsNoTracking().SingleAsync(e => e.OrderId == placed.OrderId && e.ToStatus == OrderStatus.Cancelled)).Reason.Should().Be(reason);
    }

    [PostgresFact]
    public async Task ACompanyTheRequestAlreadyTracked_IsReadFreshUnderTheLock()
    {
        // ADR-0010 B3.5 watch item: ReduceChargeAsync reads the company with FromSql in the caller's context; a company entity tracked
        // earlier in the request would come back stale (here: from before a verification) and the release would post on old totals.
        var t = await TeamAsync();
        var v = await VariantAsync(onHand: 5);
        var placed = await CreditOrderAsync(t, t.Buyers[0], (v.VariantId, 1));
        await CreditOrderAsync(t, t.Buyers[0], (v.VariantId, 1));
        await using var db = fixture.CreateContext();
        await db.Companies.SingleAsync(c => c.Id == t.Company);   // tracked before the money arrives
        var staff = await UserAsync();
        await VerifiedAsync(t, 10_000);

        var result = await CancelInAsync(db, placed.OrderId, staff);

        result.CreditReleasedMinor.Should().Be(placed.GrandTotalMinor - 10_000, "FIFO: the verified 10,000 paid this older order first");
        result.CreditMovedOnAccountMinor.Should().Be(10_000);
        await LedgerAddsUpAsync(t.Company);
    }

    [PostgresFact]
    public async Task CancellingTwice_OrFourTimesAtOnce_ReturnsTheStockAndTheCreditOnce()
    {
        var t = await TeamAsync();
        var v = await VariantAsync(onHand: 10);
        var placed = await CreditOrderAsync(t, t.Buyers[0], (v.VariantId, 4));
        var staff = await UserAsync();

        using var gate = new ManualResetEventSlim(false);
        var racers = Enumerable.Range(0, 4).Select(_ => Task.Run(async () => { gate.Wait(); return await CancelAsync(placed.OrderId, staff); })).ToList();
        gate.Set();
        var results = await Task.WhenAll(racers);

        results.Count(r => !r.AlreadyCancelled).Should().Be(1);
        results.Single(r => !r.AlreadyCancelled).CreditReleasedMinor.Should().Be(placed.GrandTotalMinor);
        results.Where(r => r.AlreadyCancelled).Should().OnlyContain(r => r.CreditReleasedMinor == 0 && r.RefundPaymentIds.Count == 0 && !r.StockReturnPending);
        results.Sum(r => r.StockReturned.Sum(s => s.Quantity)).Should().Be(4, "the stock goes back once, whoever ran the restock");
        (await StockAsync(v)).Should().Be((10, 0));
        (await LedgerAddsUpAsync(t.Company)).Count(l => l.OrderId == placed.OrderId && l.Kind == CreditLedgerKind.ChargeReduced).Should().Be(1);
        (await CreditStateAsync(t.Company)).Company.CreditUsedMinor.Should().Be(0);

        var again = await CancelAsync(placed.OrderId, staff, reason: "pressed the button again");
        again.Should().BeEquivalentTo(new { AlreadyCancelled = true, Status = "cancelled", StockReturned = Array.Empty<ReturnedStockDto>() });
        await using var db = fixture.CreateContext();
        (await db.OrderEvents.CountAsync(e => e.OrderId == placed.OrderId && e.ToStatus == OrderStatus.Cancelled)).Should().Be(1);
        (await db.AuditLogs.CountAsync(a => a.Action == OrderCancellationService.CancelledAudit && a.EntityId == placed.OrderId.ToString())).Should().Be(1);
        (await db.OutboxMessages.CountAsync(m => m.AggregateId == placed.OrderId && m.EventType == nameof(ConfirmedOrderCancelledEvent))).Should().Be(1);
    }

    [PostgresFact]
    public async Task OnlyAConfirmedProcessingOrPackedOrder_CanBeCancelledHere_NothingElseChanges()
    {
        var buyer = await UserAsync();
        var v = await VariantAsync(onHand: 5);
        PlacedOrder pending;
        await using (var db = fixture.CreateContext())
            pending = await Orders(db).PlaceAsync(new PlaceOrderRequest(buyer, Key(), [new PlaceOrderLine(v.VariantId, 1)]));
        var t = await TeamAsync();
        var shipped = await CreditOrderAsync(t, t.Buyers[0], (v.VariantId, 1));
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET status = 'Shipped' WHERE id = {shipped.OrderId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sub_orders SET status = 'Shipped' WHERE order_id = {shipped.OrderId}");
        }
        var packed = await CreditOrderAsync(t, t.Buyers[0], (v.VariantId, 1));
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET status = 'Packed' WHERE id = {packed.OrderId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sub_orders SET status = 'Packed' WHERE order_id = {packed.OrderId}");
        }
        var staff = await UserAsync();
        var before = await CreditStateAsync(t.Company);

        (await Assert.ThrowsAsync<ConflictException>(() => CancelAsync(pending.OrderId, staff))).Code.Should().Be("ORDER_NOT_CANCELLABLE");
        (await Assert.ThrowsAsync<ConflictException>(() => CancelAsync(shipped.OrderId, staff))).Code.Should().Be("ORDER_NOT_CANCELLABLE");
        await Assert.ThrowsAsync<ValidationException>(() => CancelAsync(packed.OrderId, staff, reason: " no "));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => CancelAsync(Guid.NewGuid(), staff));

        (await OrderAsync(pending.OrderId)).Status.Should().Be(OrderStatus.PendingPayment);
        (await OrderAsync(shipped.OrderId)).Status.Should().Be(OrderStatus.Shipped);
        (await CreditStateAsync(t.Company)).Lines.Should().HaveCount(before.Lines.Count, "a refused cancel releases nothing");
        (await StockAsync(v)).Should().Be((3, 1), "the pending order still holds its unit; the two credit orders took theirs");

        (await CancelAsync(packed.OrderId, staff)).Should().BeEquivalentTo(new { Status = "cancelled", CreditReleasedMinor = packed.GrandTotalMinor });
        (await StockAsync(v)).Should().Be((4, 1));
    }

    [PostgresFact]
    public async Task AConfirmedOrderWhoseHoldWasNeverCommitted_HasItsHoldReleased_NotRestocked()
    {
        // The E7.3 crash window: the confirmation committed but the process died before inventory.CommitAsync. Nothing was deducted, so
        // the cancel releases the hold instead of restocking (a restock would add stock that never left).
        var v = await VariantAsync(onHand: 5);
        PlacedOrder placed;
        await using (var db = fixture.CreateContext())
            placed = await Orders(db).PlaceAsync(new PlaceOrderRequest(await UserAsync(), Key(), [new PlaceOrderLine(v.VariantId, 2)]));
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET status = 'Confirmed', confirmed_at = now() WHERE id = {placed.OrderId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sub_orders SET status = 'Confirmed' WHERE order_id = {placed.OrderId}");
        }
        (await StockAsync(v)).Should().Be((5, 2));

        var result = await CancelAsync(placed.OrderId, await UserAsync());

        result.Should().BeEquivalentTo(new { Status = "cancelled", StockReturned = Array.Empty<ReturnedStockDto>(), StockReturnPending = false });
        (await StockAsync(v)).Should().Be((5, 0));
        await using var check = fixture.CreateContext();
        (await check.StockMovements.CountAsync(m => m.ReferenceId == placed.OrderId)).Should().Be(0);
        (await check.StockReservations.AsNoTracking().Where(r => r.ReferenceId == placed.OrderId).Select(r => r.Status).ToListAsync())
            .Should().OnlyContain(s => s == ReservationStatus.Cancelled);
    }

    [PostgresFact]
    public async Task AConfirmedOrderWhoseHoldExpired_HasNothingToReturn()
    {
        // The same crash window, after the sweeper let the hold lapse: nothing was deducted and nothing is held, so nothing goes back.
        var v = await VariantAsync(onHand: 5);
        PlacedOrder placed;
        await using (var db = fixture.CreateContext())
            placed = await Orders(db).PlaceAsync(new PlaceOrderRequest(await UserAsync(), Key(), [new PlaceOrderLine(v.VariantId, 2)]));
        await using (var db = fixture.CreateContext())
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET status = 'Confirmed', confirmed_at = now() WHERE id = {placed.OrderId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sub_orders SET status = 'Confirmed' WHERE order_id = {placed.OrderId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stock_reservations SET status = 'expired' WHERE reference_id = {placed.OrderId}");
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE inventory_items SET reserved = reserved - 2 WHERE id = {v.InventoryItemId}");
        }

        (await CancelAsync(placed.OrderId, await UserAsync())).Should().BeEquivalentTo(new
        {
            Status = "cancelled",
            StockReturned = Array.Empty<ReturnedStockDto>(),
            StockReturnPending = false
        });
        (await StockAsync(v)).Should().Be((5, 0));
    }

    [PostgresFact]
    public async Task AStockReturnThatFailedAfterTheCommit_IsFinishedByCallingAgain()
    {
        // The cancel and the credit release are one transaction; the restock runs after it (it opens its own). If it fails, the order
        // stays cancelled and the answer says the stock is pending; calling again finishes it, once.
        var t = await TeamAsync();
        var v = await VariantAsync(onHand: 10);
        var placed = await CreditOrderAsync(t, t.Buyers[0], (v.VariantId, 3));
        var staff = await UserAsync();

        var first = await CancelAsync(placed.OrderId, staff, wrap: inner => new FlakyRestock(inner, failures: 1));

        first.Should().BeEquivalentTo(new
        {
            AlreadyCancelled = false,
            Status = "cancelled",
            CreditReleasedMinor = placed.GrandTotalMinor,
            StockReturned = Array.Empty<ReturnedStockDto>(),
            StockReturnPending = true
        });
        (await StockAsync(v)).Should().Be((7, 0));
        (await CreditStateAsync(t.Company)).Company.CreditUsedMinor.Should().Be(0, "the credit went with the cancel, not with the stock");

        var retry = await CancelAsync(placed.OrderId, staff);

        retry.Should().BeEquivalentTo(new
        {
            AlreadyCancelled = true,
            CreditReleasedMinor = 0L,
            StockReturned = new[] { new ReturnedStockDto(v.VariantId, 3) },
            StockReturnPending = false
        });
        (await StockAsync(v)).Should().Be((10, 0));
        (await CancelAsync(placed.OrderId, staff)).StockReturned.Should().BeEmpty();
        (await StockAsync(v)).Should().Be((10, 0));
    }

    // ---- CS16: lock order -----------------------------------------------------------------------------------------------

    /// <summary>Waits until some other session is blocked by the session <paramref name="holderPid"/>.</summary>
    private async Task WaitUntilBlockedByAsync(int holderPid)
    {
        await using var db = fixture.CreateContext();
        for (var i = 0; i < 300; i++)
        {
            var blocked = await db.Database.SqlQuery<int>(
                $"SELECT count(*)::int AS \"Value\" FROM pg_stat_activity WHERE {holderPid} = ANY(pg_blocking_pids(pid))").SingleAsync();
            if (blocked > 0) return;
            await Task.Delay(50);
        }
        throw new TimeoutException("Nobody queued behind the test's lock.");
    }

    private static async Task<int> PidAsync(MechwareDbContext db) =>
        await db.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();

    [PostgresFact]
    public async Task ACancelHoldingTheOrder_NeverBlocksAVerificationThatWritesALineNamingIt()
    {
        // A verification holds the company and inserts ledger lines naming open orders; each line's foreign key takes FOR KEY SHARE on
        // its order. The cancel holds its order and then waits for the company. With FOR UPDATE on the order these two wait on each other
        // (40P01, ADR-0010 B3.5 notes §1); with FOR NO KEY UPDATE the verification's key-share lock is granted at once.
        var t = await TeamAsync();
        var v = await VariantAsync(onHand: 5);
        var placed = await CreditOrderAsync(t, t.Buyers[0], (v.VariantId, 1));
        var staff = await UserAsync();
        Task<CancelledOrderDto> cancel;
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM companies WHERE id = {t.Company} FOR UPDATE");   // the verification
            cancel = Task.Run(() => CancelAsync(placed.OrderId, staff));
            await WaitUntilBlockedByAsync(await PidAsync(db));   // the cancel holds the order and waits for the company

            var keyShare = db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {placed.OrderId} FOR KEY SHARE");
            (await Task.WhenAny(keyShare, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(keyShare, "the cancel must not block a ledger line naming its order");
            await keyShare;
            await tx.CommitAsync();
        }
        (await cancel).CreditReleasedMinor.Should().Be(placed.GrandTotalMinor);
        await LedgerAddsUpAsync(t.Company);
    }

    [PostgresFact]
    public async Task ACancel_AndAPaymentWebhookOfTheSameOrder_TakeThePaymentFirst_NeverDeadlock()
    {
        // The webhook locks the payment, then the order. The cancel takes the order's payments first, then the order: holding the payment
        // here, the cancel waits for it, so the webhook's next step (the order lock) is granted. Order first would deadlock.
        var v = await VariantAsync(onHand: 5);
        var (placed, payment, _) = await PaidOrderAsync(await UserAsync(), v.VariantId);
        var staff = await UserAsync();
        Task<CancelledOrderDto> cancel;
        await using (var db = fixture.CreateContext())
        {
            await using var tx = await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM payments WHERE id = {payment} FOR UPDATE");   // the webhook
            cancel = Task.Run(() => CancelAsync(placed.OrderId, staff));
            await WaitUntilBlockedByAsync(await PidAsync(db));

            var orderLock = db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM orders WHERE id = {placed.OrderId} FOR UPDATE");
            (await Task.WhenAny(orderLock, Task.Delay(TimeSpan.FromSeconds(10)))).Should().BeSameAs(orderLock, "the cancel must not hold the order yet");
            await orderLock;
            await tx.CommitAsync();
        }
        (await cancel).RefundPaymentIds.Should().Equal(payment);

        // A duplicate webhook for the captured payment arriving afterwards changes nothing.
        await using var check = fixture.CreateContext();
        var payments = new PaymentService(check, [_mock], new InventoryService(check, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
            ShippingTestKit.Deadlines(check));
        var providerRef = (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == payment)).ProviderRef;
        await WebhookAsync(payments, providerRef);
        (await check.Payments.AsNoTracking().SingleAsync(p => p.Id == payment)).Status.Should().Be(PaymentStatus.NeedsRefund);
        (await check.Orders.AsNoTracking().SingleAsync(o => o.Id == placed.OrderId)).Status.Should().Be(OrderStatus.Cancelled);
    }

    [PostgresFact]
    public async Task CancelVerifyBounceAndPlace_InParallel_NeverDeadlock_AndTheLedgerAddsUp()
    {
        // 8 cancels of real confirmed credit orders (payments → order → company → promotions, then the restock), 6 verifications and
        // 4 bounces (payment → company) and 6 new credit orders (company), one company, all at once: no 40P01, no lost update, every
        // cancelled order owes nothing, its stock is back, and the ledger chain ends at the cached totals.
        var t = await TeamAsync(buyers: 6);
        var stock = new List<SeededVariant>();
        for (var i = 0; i < 8; i++) stock.Add(await VariantAsync(onHand: 10, price: 20_000));
        var fresh = new List<SeededVariant>();
        for (var i = 0; i < 6; i++) fresh.Add(await VariantAsync(onHand: 5, price: 20_000));
        var cancels = new List<PlacedOrder>();
        foreach (var v in stock) cancels.Add(await CreditOrderAsync(t, t.Buyers[0], (v.VariantId, 2)));
        var toBounce = new List<Guid>();
        for (var i = 0; i < 4; i++) toBounce.Add(await VerifiedAsync(t, 30_000));   // FIFO: they pay the orders cancelled below
        var toVerify = new List<Guid>();
        for (var i = 0; i < 6; i++) toVerify.Add(await RecordedAsync(t, 25_000));
        var (staff, verifier, bouncer) = (await UserAsync(), await UserAsync(), await UserAsync());

        using var gate = new ManualResetEventSlim(false);
        var work = new List<Task>();
        work.AddRange(cancels.Select(o => Task.Run(async () => { gate.Wait(); (await CancelAsync(o.OrderId, staff)).StockReturnPending.Should().BeFalse(); })));
        work.AddRange(toVerify.Select(p => Task.Run(async () => { gate.Wait(); await CreditPayments(verifier, s => s.VerifyAsync(p, new VerifyCreditPaymentRequest(Today))); })));
        work.AddRange(toBounce.Select(p => Task.Run(async () => { gate.Wait(); await CreditPayments(bouncer, s => s.BounceAsync(p, new BounceCreditPaymentRequest("cheque returned unpaid"))); })));
        work.AddRange(t.Buyers.Select((b, i) => Task.Run(async () => { gate.Wait(); await CreditOrderAsync(t, b, (fresh[i].VariantId, 1)); })));
        gate.Set();
        var all = Task.WhenAll(work);
        (await Task.WhenAny(all, Task.Delay(TimeSpan.FromSeconds(120)))).Should().BeSameAs(all, "a deadlock would hang or abort");
        await all;   // any 40P01, lost update or broken chain throws here

        var lines = await LedgerAddsUpAsync(t.Company);
        foreach (var o in cancels)
        {
            lines.Where(l => l.OrderId == o.OrderId).Sum(l => l.ReceivableDeltaMinor).Should().Be(0, "a cancelled order owes nothing, whatever ran before");
            (await OrderAsync(o.OrderId)).Status.Should().Be(OrderStatus.Cancelled);
        }
        foreach (var v in stock) (await StockAsync(v)).Should().Be((10, 0));
        foreach (var v in fresh) (await StockAsync(v)).Should().Be((4, 0));
        await using var db = fixture.CreateContext();
        (await db.CreditPayments.CountAsync(p => toBounce.Contains(p.Id) && p.Status == CreditPaymentStatus.Bounced)).Should().Be(4);
        (await db.CreditPayments.CountAsync(p => toVerify.Contains(p.Id) && p.Status == CreditPaymentStatus.Verified)).Should().Be(6);
    }
}
