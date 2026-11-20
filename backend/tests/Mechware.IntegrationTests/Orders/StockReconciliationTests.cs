using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Inventory;
using Mechware.Application.Orders;
using Mechware.Domain.Inventory;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Credit;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Persistence;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Orders;

/// <summary>
/// E7.3 against a real database: the sweep finishes the stock step of orders a crash left half done (A: commit an uncommitted hold of
/// an order past confirmation; B: put back the stock of a cancelled order whose return is pending; C: release the hold of a cancelled
/// order), once, only outside the grace period, one sweeper at a time, and safely next to a staff cancel.
/// </summary>
public class StockReconciliationTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private static DateTime Later => DateTime.UtcNow.AddMinutes(10);   // past the 5-minute grace period of anything changed now
    private static string Key() => "key-" + Guid.NewGuid().ToString("N");

    private sealed class Staff(Guid id) : ICurrentUserService
    {
        public Guid? UserId => id;
        public string? Email => null;
        public Guid? SellerId => null;
        public string? IpAddress => "203.0.113.73";
        public string? UserAgent => null;
        public string? CorrelationId => "corr-sweep";
        public bool HasPermission(string permission) => true;
    }

    /// <summary>Restocks after the first <paramref name="succeed"/> calls fail (a crash in the middle of the cancel's stock step).</summary>
    private sealed class RestockFailsAfter(IInventoryService inner, int succeed) : IInventoryService
    {
        private int _left = succeed;
        public Task<RestockResult> RestockAsync(RestockRequest request, CancellationToken ct = default) =>
            Interlocked.Decrement(ref _left) < 0 ? throw new TimeoutException("the database went away") : inner.RestockAsync(request, ct);
        public Task<ReserveStockResult> ReserveAsync(ReserveStockRequest request, CancellationToken ct = default) => inner.ReserveAsync(request, ct);
        public Task<int> CommitAsync(Guid referenceId, CancellationToken ct = default) => inner.CommitAsync(referenceId, ct);
        public Task<int> CancelAsync(Guid referenceId, CancellationToken ct = default) => inner.CancelAsync(referenceId, ct);
        public Task<int> ReleaseExpiredAsync(DateTime nowUtc, int batchSize = 500, CancellationToken ct = default) => inner.ReleaseExpiredAsync(nowUtc, batchSize, ct);
    }

    private async Task<StockReconciliationPass> SweepAsync(DateTime? now = null, int batch = 100)
    {
        await using var db = fixture.CreateContext();
        var service = new StockReconciliationService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System),
            new StockReconciliationOptions { BatchSize = batch }, TimeProvider.System);
        return await service.RunOnceAsync(now ?? Later);
    }

    private async Task<CancelledOrderDto> StaffCancelAsync(Guid order, Func<IInventoryService, IInventoryService>? wrap = null)
    {
        await using var db = fixture.CreateContext();
        var user = new Staff(await TestUsers.CreateAsync(fixture));
        var inventory = new InventoryService(db, new NoopLockProvider(), TimeProvider.System, user: user);
        return await new OrderCancellationService(db, wrap is null ? inventory : wrap(inventory), new CreditLedgerService(db, user, TimeProvider.System),
            user, TimeProvider.System).CancelConfirmedAsync(order, new CancelConfirmedOrderRequest("customer called: wrong part ordered"));
    }

    private async Task<SeededVariant> VariantAsync(int onHand)
    {
        await using var db = fixture.CreateContext();
        return await CatalogTestData.SeedVariantAsync(db, onHand, priceMinor: 100_000);
    }

    private async Task<(int OnHand, int Reserved)> StockAsync(SeededVariant v)
    {
        await using var db = fixture.CreateContext();
        var item = await db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == v.InventoryItemId);
        return (item.OnHand, item.Reserved);
    }

    /// <summary>A personal order waiting for payment: its stock is held (active), nothing deducted.</summary>
    private async Task<Guid> HeldOrderAsync(params (SeededVariant V, int Quantity)[] lines)
    {
        await using var db = fixture.CreateContext();
        var orders = new OrderService(db, new InventoryService(db, new NoopLockProvider(), TimeProvider.System), TimeProvider.System,
            ShippingTestKit.DevelopmentQuoter(db), new Infrastructure.Pricing.PriceListResolver(db));
        var placed = await orders.PlaceAsync(new PlaceOrderRequest(await TestUsers.CreateAsync(fixture), Key(),
            lines.Select(l => new PlaceOrderLine(l.V.VariantId, l.Quantity)).ToList()));
        placed.Status.Should().Be("pending_payment");
        return placed.OrderId;
    }

    /// <summary>The order's state was saved but the process died before the stock step that follows it.</summary>
    private async Task CrashedInAsync(Guid order, string status, DateTime? changedAt = null)
    {
        await using var db = fixture.CreateContext();
        var at = changedAt ?? DateTime.UtcNow;
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET status = {status}, confirmed_at = now(), updated_at = {at} WHERE id = {order}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE sub_orders SET status = {status} WHERE order_id = {order}");
    }

    /// <summary>A confirmed order whose stock was deducted (committed), as after a normal confirmation.</summary>
    private async Task<Guid> CommittedOrderAsync(params (SeededVariant V, int Quantity)[] lines)
    {
        var order = await HeldOrderAsync(lines);
        await CrashedInAsync(order, "Confirmed");
        await using var db = fixture.CreateContext();
        (await new InventoryService(db, new NoopLockProvider(), TimeProvider.System).CommitAsync(order)).Should().Be(lines.Length);
        return order;
    }

    private async Task<List<JsonElement>> AuditsAsync(Guid order)
    {
        await using var db = fixture.CreateContext();
        var rows = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == StockReconciliationService.AuditAction && a.EntityId == order.ToString()).ToListAsync();
        rows.Should().OnlyContain(a => a.ActorId == null && a.EntityName == "orders");
        return rows.Select(a => JsonDocument.Parse(a.NewValuesJson!).RootElement).ToList();
    }

    private async Task<List<ReservationStatus>> HoldsAsync(Guid order)
    {
        await using var db = fixture.CreateContext();
        return await db.StockReservations.AsNoTracking().Where(r => r.ReferenceId == order).Select(r => r.Status).ToListAsync();
    }

    private async Task<List<StockMovement>> MovementsAsync(Guid order)
    {
        await using var db = fixture.CreateContext();
        return await db.StockMovements.AsNoTracking().Where(m => m.ReferenceId == order).ToListAsync();
    }

    [PostgresFact]
    public async Task EachCase_IsFixedExactlyOnce_AcrossTwoPassesOfTwoWorkersInParallel()
    {
        var va = await VariantAsync(5);
        var a = await HeldOrderAsync((va, 2));
        await CrashedInAsync(a, "Confirmed");                               // A: confirmed, hold never committed
        var vb = await VariantAsync(10);
        var b = await CommittedOrderAsync((vb, 3));
        (await StaffCancelAsync(b, inner => new RestockFailsAfter(inner, 0))).StockReturnPending.Should().BeTrue();   // B
        var vc = await VariantAsync(5);
        var c = await HeldOrderAsync((vc, 2));
        await CrashedInAsync(c, "Cancelled");                               // C: cancelled (e.g. unpaid expiry), hold never released

        for (var pass = 0; pass < 2; pass++)
        {
            var passes = await Task.WhenAll(SweepAsync(), SweepAsync());
            passes.Should().OnlyContain(p => p.Failed == 0);
        }

        (await StockAsync(va)).Should().Be((3, 0), "A: the held stock is deducted, not put back");
        (await HoldsAsync(a)).Should().OnlyContain(s => s == ReservationStatus.Fulfilled);
        (await MovementsAsync(a)).Should().BeEmpty();
        (await AuditsAsync(a)).Should().ContainSingle().Which.GetProperty("case").GetString().Should().Be("commit_hold");

        (await StockAsync(vb)).Should().Be((10, 0), "B: the deducted stock is back, once");
        (await MovementsAsync(b)).Should().ContainSingle().Which.Should().BeEquivalentTo(new { Delta = 3, Reason = RestockReason.OrderCancelled, ActorId = (Guid?)null });
        var audit = (await AuditsAsync(b)).Should().ContainSingle().Subject;
        audit.GetProperty("case").GetString().Should().Be("return_stock");
        audit.GetProperty("actor").GetString().Should().Be("system");

        (await StockAsync(vc)).Should().Be((5, 0), "C: the hold is released, nothing restocked");
        (await HoldsAsync(c)).Should().OnlyContain(s => s == ReservationStatus.Cancelled);
        (await MovementsAsync(c)).Should().BeEmpty();
        (await AuditsAsync(c)).Should().ContainSingle().Which.GetProperty("case").GetString().Should().Be("release_hold");
    }

    [PostgresFact]
    public async Task APartialPutBack_IsFinished_OnlyTheMissingVariantGoesBack()
    {
        var v1 = await VariantAsync(10);
        var v2 = await VariantAsync(10);
        var order = await CommittedOrderAsync((v1, 3), (v2, 4));
        var first = await StaffCancelAsync(order, inner => new RestockFailsAfter(inner, 1));   // one variant back, then a crash
        first.StockReturnPending.Should().BeTrue();
        first.StockReturned.Should().ContainSingle();
        var done = first.StockReturned[0].VariantId;
        var missing = done == v1.VariantId ? v2 : v1;

        (await SweepAsync()).Failed.Should().Be(0);

        (await StockAsync(v1)).Should().Be((10, 0));
        (await StockAsync(v2)).Should().Be((10, 0));
        (await MovementsAsync(order)).Should().HaveCount(2);
        var returned = (await AuditsAsync(order)).Should().ContainSingle().Subject.GetProperty("detail").GetProperty("returned");
        returned.GetArrayLength().Should().Be(1);
        returned[0].GetProperty("variantId").GetGuid().Should().Be(missing.VariantId);
        (await SweepAsync()).Should().Be(new StockReconciliationPass(false, 0, 0));
        (await StockAsync(missing)).Should().Be((10, 0));
    }

    [PostgresFact]
    public async Task AnOrderInsideTheGracePeriod_IsLeftAlone_UntilItHasPassed()
    {
        var v = await VariantAsync(5);
        var order = await HeldOrderAsync((v, 2));
        await CrashedInAsync(order, "Confirmed", changedAt: DateTime.UtcNow);

        await SweepAsync(DateTime.UtcNow.AddMinutes(2));   // a request changed it 2 minutes ago: it may still be in flight

        (await StockAsync(v)).Should().Be((5, 2));
        (await HoldsAsync(order)).Should().OnlyContain(s => s == ReservationStatus.Active);
        (await AuditsAsync(order)).Should().BeEmpty();

        await SweepAsync(DateTime.UtcNow.AddMinutes(6));
        (await StockAsync(v)).Should().Be((3, 0));
    }

    [PostgresFact]
    public async Task AShippedOrderWithAnActiveHold_IsCommitted_NotRestocked()
    {
        var v = await VariantAsync(5);
        var order = await HeldOrderAsync((v, 2));
        await CrashedInAsync(order, "Shipped");

        (await SweepAsync()).Failed.Should().Be(0);

        (await StockAsync(v)).Should().Be((3, 0));
        (await HoldsAsync(order)).Should().OnlyContain(s => s == ReservationStatus.Fulfilled);
        (await MovementsAsync(order)).Should().BeEmpty();
        (await AuditsAsync(order)).Should().ContainSingle().Which.GetProperty("case").GetString().Should().Be("commit_hold");
    }

    [PostgresFact]
    public async Task RunningTheSweepAgain_ChangesNothing()
    {
        var v = await VariantAsync(10);
        var order = await CommittedOrderAsync((v, 3));
        await StaffCancelAsync(order, inner => new RestockFailsAfter(inner, 0));
        (await SweepAsync()).Failed.Should().Be(0);

        async Task<object> SnapshotAsync()
        {
            await using var db = fixture.CreateContext();
            return new
            {
                Movements = await db.StockMovements.CountAsync(),
                Audits = await db.AuditLogs.CountAsync(),
                OnHand = await db.InventoryItems.SumAsync(i => i.OnHand),
                Reserved = await db.InventoryItems.SumAsync(i => i.Reserved),
                Active = await db.StockReservations.CountAsync(r => r.Status == ReservationStatus.Active)
            };
        }

        var before = await SnapshotAsync();
        (await SweepAsync()).Should().Be(new StockReconciliationPass(false, 0, 0));
        (await SweepAsync()).Should().Be(new StockReconciliationPass(false, 0, 0));
        (await SnapshotAsync()).Should().BeEquivalentTo(before);
        (await StockAsync(v)).Should().Be((10, 0));
    }

    [PostgresFact]
    public async Task AnOrderWhoseStockIsAlreadyBack_NeverTakesASlotInTheBatch()
    {
        // A cancelled order whose stock went back normally is not a case: re-picking it every pass would be harmless (the restock is a
        // replay) but would fill the batch with finished orders and starve the real crash cases behind them.
        await SweepAsync();
        var vDone = await VariantAsync(10);
        var finished = await CommittedOrderAsync((vDone, 1));
        (await StaffCancelAsync(finished)).StockReturnPending.Should().BeFalse();
        await using (var db = fixture.CreateContext())
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE orders SET updated_at = {DateTime.UtcNow.AddHours(-1)} WHERE id = {finished}");
        var v = await VariantAsync(5);
        var crashed = await HeldOrderAsync((v, 2));
        await CrashedInAsync(crashed, "Confirmed");

        (await SweepAsync(batch: 1)).Should().Be(new StockReconciliationPass(false, 1, 0));

        (await StockAsync(v)).Should().Be((3, 0));
        (await AuditsAsync(finished)).Should().BeEmpty();
    }

    [PostgresFact]
    public async Task WhileAnotherWorkerSweeps_ThePassIsSkipped_AndTouchesNothing()
    {
        var v = await VariantAsync(5);
        var order = await HeldOrderAsync((v, 2));
        await CrashedInAsync(order, "Confirmed");

        await using (var other = fixture.CreateContext())
        {
            await other.Database.OpenConnectionAsync();
            await other.Database.ExecuteSqlRawAsync("SELECT pg_advisory_lock(hashtextextended('stock-reconciliation', 0))");

            (await SweepAsync()).Should().Be(new StockReconciliationPass(true, 0, 0));
            (await StockAsync(v)).Should().Be((5, 2));

            await other.Database.ExecuteSqlRawAsync("SELECT pg_advisory_unlock(hashtextextended('stock-reconciliation', 0))");
        }

        (await SweepAsync()).Skipped.Should().BeFalse();
        (await StockAsync(v)).Should().Be((3, 0));
    }

    [PostgresFact]
    public async Task TheSweepAndAStaffCancelOfTheSameOrder_InParallel_NeverFailOrDeadlock_AndTheStockAddsUp()
    {
        for (var round = 0; round < 4; round++)
        {
            var v = await VariantAsync(5);
            var order = await HeldOrderAsync((v, 2));
            await CrashedInAsync(order, "Confirmed", changedAt: DateTime.UtcNow.AddMinutes(-10));

            var cancel = StaffCancelAsync(order);
            var sweeps = Task.WhenAll(SweepAsync(DateTime.UtcNow), SweepAsync(DateTime.UtcNow));
            var finished = Task.WhenAll(cancel, sweeps);
            (await Task.WhenAny(finished, Task.Delay(TimeSpan.FromSeconds(30)))).Should().BeSameAs(finished, "no deadlock");
            (await cancel).Should().BeEquivalentTo(new { Status = "cancelled", StockReturnPending = false });
            (await sweeps).Should().OnlyContain(p => p.Failed == 0);

            (await SweepAsync()).Failed.Should().Be(0);
            (await StockAsync(v)).Should().Be((5, 0), $"round {round}: committed then returned, or released, never both or neither");
            (await HoldsAsync(order)).Should().NotContain(ReservationStatus.Active);
            (await MovementsAsync(order)).Sum(m => m.Delta).Should().Be((await HoldsAsync(order)).Contains(ReservationStatus.Fulfilled) ? 2 : 0);
        }
    }
}
