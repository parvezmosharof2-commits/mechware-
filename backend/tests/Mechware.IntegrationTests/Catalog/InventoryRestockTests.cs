using System.Collections.Concurrent;
using System.Text.Json;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Inventory;
using Mechware.Application.Search;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Inventory;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Search;
using Mechware.IntegrationTests.Support;

namespace Mechware.IntegrationTests.Catalog;

/// <summary>
/// E7.1 restock: stock goes back on the shelf atomically with an append-only movement and an audit row, never touches holds,
/// is idempotent per (reference, reason, variant), and races safely with reservations under the same row locks.
/// </summary>
public class InventoryRestockTests(PostgresDatabaseFixture fixture) : IClassFixture<PostgresDatabaseFixture>
{
    private sealed class RecordingReindexQueue : ISearchReindexQueue
    {
        public ConcurrentBag<Guid> Products { get; } = [];

        public Task EnqueueProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
        {
            foreach (var id in productIds) Products.Add(id);
            return Task.CompletedTask;
        }
    }

    private static InventoryService Service(MechwareDbContext db, ISearchReindexQueue? reindex = null) =>
        new(db, new NoopLockProvider(), TimeProvider.System, reindex: reindex);

    private async Task<T> With<T>(Func<InventoryService, Task<T>> action)
    {
        await using var db = fixture.CreateContext();
        return await action(Service(db));
    }

    private async Task<InventoryItem> ItemAsync(Guid id)
    {
        await using var db = fixture.CreateContext();
        return await db.InventoryItems.AsNoTracking().SingleAsync(i => i.Id == id);
    }

    private static RestockRequest Receipt(Guid variant, int quantity, Guid? reference = null) =>
        new(variant, quantity, RestockReason.StockReceived, "stock_receipt", reference ?? Guid.NewGuid());

    /// <summary>A confirmed order: its hold is committed, so its stock has left the shelf.</summary>
    private async Task<Guid> ConfirmedOrderAsync(Guid variant, int quantity)
    {
        var order = Guid.NewGuid();
        (await With(s => s.ReserveAsync(new ReserveStockRequest(order, [new ReservationLine(variant, quantity)])))).Succeeded.Should().BeTrue();
        (await With(s => s.CommitAsync(order))).Should().BeGreaterThan(0);
        return order;
    }

    [PostgresFact]
    public async Task Restock_RaisesOnHand_NeverTouchesReserved()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 10);
        (await With(s => s.ReserveAsync(new ReserveStockRequest(Guid.NewGuid(), [new ReservationLine(seeded.VariantId, 3)])))).Succeeded.Should().BeTrue();

        var result = await With(s => s.RestockAsync(Receipt(seeded.VariantId, 5)));

        result.AlreadyApplied.Should().BeFalse();
        result.Lines.Should().ContainSingle().Which.Should().Be(new RestockedLine(seeded.WarehouseId, 5, 15));
        var item = await ItemAsync(seeded.InventoryItemId);
        (item.OnHand, item.Reserved, item.Available).Should().Be((15, 3, 12));
    }

    [PostgresFact]
    public async Task Replay_OfTheSameReference_AddsStockOnce_AndAnotherReferenceAddsAgain()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 2);
        var reference = Guid.NewGuid();

        (await With(s => s.RestockAsync(Receipt(seeded.VariantId, 4, reference)))).AlreadyApplied.Should().BeFalse();
        var replay = await With(s => s.RestockAsync(Receipt(seeded.VariantId, 4, reference)));
        replay.AlreadyApplied.Should().BeTrue();
        replay.Lines.Should().ContainSingle().Which.OnHandAfter.Should().Be(6);
        (await ItemAsync(seeded.InventoryItemId)).OnHand.Should().Be(6, "a replayed restock never adds stock twice");

        var differentQuantity = () => With(s => s.RestockAsync(Receipt(seeded.VariantId, 5, reference)));
        (await differentQuantity.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("RESTOCK_CONFLICT");

        (await With(s => s.RestockAsync(Receipt(seeded.VariantId, 4)))).AlreadyApplied.Should().BeFalse();
        // Same reference, another reason: a different operation (e.g. a correction after a receipt).
        (await With(s => s.RestockAsync(Receipt(seeded.VariantId, 1, reference) with { Reason = RestockReason.Correction }))).AlreadyApplied.Should().BeFalse();
        (await ItemAsync(seeded.InventoryItemId)).OnHand.Should().Be(11);

        await using var db = fixture.CreateContext();
        (await db.StockMovements.CountAsync(m => m.InventoryItemId == seeded.InventoryItemId)).Should().Be(3);
    }

    [PostgresTheory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(RestockRequest.MaxQuantity + 1)]
    public async Task Quantity_OutsideOneToMax_IsRefused(int quantity)
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 1);

        var act = () => With(s => s.RestockAsync(Receipt(seeded.VariantId, quantity)));

        (await act.Should().ThrowAsync<ValidationException>()).Which.Message.Should().Contain("Quantity");
        (await ItemAsync(seeded.InventoryItemId)).OnHand.Should().Be(1);
    }

    [PostgresFact]
    public async Task Restock_WithoutAReasonOrReference_IsRefused()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 1);
        var good = Receipt(seeded.VariantId, 1);

        foreach (var bad in new[]
                 {
                     good with { Reason = default },
                     good with { Reason = (RestockReason)99 },
                     good with { ReferenceType = "" },
                     good with { ReferenceType = "Order; DROP" },
                     good with { ReferenceId = Guid.Empty },
                     good with { Reason = RestockReason.OrderCancelled }, // a cancelled order restocks against "order"
                     good with { WarehouseId = Guid.Empty },
                     good with { VariantId = Guid.Empty }
                 })
        {
            var act = () => With(s => s.RestockAsync(bad));
            await act.Should().ThrowAsync<ValidationException>($"{bad} must be refused");
        }

        (await ItemAsync(seeded.InventoryItemId)).OnHand.Should().Be(1);
    }

    [PostgresFact]
    public async Task UnknownVariant_OrWarehouse_IsNotFound()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 1);

        var unknownVariant = () => With(s => s.RestockAsync(Receipt(Guid.NewGuid(), 1)));
        await unknownVariant.Should().ThrowAsync<KeyNotFoundException>();
        var unknownWarehouse = () => With(s => s.RestockAsync(Receipt(seeded.VariantId, 1) with { WarehouseId = Guid.NewGuid() }));
        await unknownWarehouse.Should().ThrowAsync<KeyNotFoundException>();
    }

    [PostgresFact]
    public async Task Restock_WritesAMovementAndAnAuditRow_AndTheLedgerIsAppendOnly()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 0);
        var reference = Guid.NewGuid();
        var actor = Guid.NewGuid();

        await With(s => s.RestockAsync(new RestockRequest(seeded.VariantId, 7, RestockReason.Correction, "stock_count", reference, ActorUserId: actor)));
        await With(s => s.RestockAsync(Receipt(seeded.VariantId, 2)));

        await using var db = fixture.CreateContext();
        var movement = await db.StockMovements.AsNoTracking().SingleAsync(m => m.ReferenceId == reference);
        (movement.InventoryItemId, movement.Delta, movement.Reason, movement.ReferenceType, movement.ActorId)
            .Should().Be((seeded.InventoryItemId, 7, RestockReason.Correction, "stock_count", (Guid?)actor));

        var audits = await db.AuditLogs.AsNoTracking()
            .Where(a => a.Action == "inventory.restocked" && a.EntityId == seeded.VariantId.ToString()).ToListAsync();
        audits.Should().HaveCount(2);
        var byStaff = audits.Single(a => a.ActorId == actor);
        byStaff.EntityName.Should().Be("product_variants");
        var values = JsonDocument.Parse(byStaff.NewValuesJson!).RootElement;
        values.GetProperty("Quantity").GetInt32().Should().Be(7);
        values.GetProperty("Reason").GetString().Should().Be("Correction");
        values.GetProperty("ReferenceType").GetString().Should().Be("stock_count");
        values.GetProperty("ReferenceId").GetGuid().Should().Be(reference);
        values.GetProperty("Actor").GetString().Should().Be(actor.ToString());
        values.GetProperty("Lines")[0].GetProperty("WarehouseId").GetGuid().Should().Be(seeded.WarehouseId);
        JsonDocument.Parse(audits.Single(a => a.ActorId is null).NewValuesJson!).RootElement
            .GetProperty("Actor").GetString().Should().Be("system");

        var update = () => db.Database.ExecuteSqlInterpolatedAsync($"UPDATE stock_movements SET delta = 700 WHERE id = {movement.Id}");
        await update.Should().ThrowAsync<Exception>().WithMessage("*append-only*");
        var delete = () => db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM stock_movements WHERE id = {movement.Id}");
        await delete.Should().ThrowAsync<Exception>().WithMessage("*append-only*");
    }

    [PostgresFact]
    public async Task Restock_RequestsAReindex_SoAnOutOfStockProductShowsInStockInSearch()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 0);
        await using (var before = fixture.CreateContext())
            (await new ProductSearchDocumentLoader(before, TimeProvider.System).LoadAsync([seeded.ProductId])).Single().InStock.Should().BeFalse();

        var queue = new RecordingReindexQueue();
        await using (var db = fixture.CreateContext()) await Service(db, queue).RestockAsync(Receipt(seeded.VariantId, 3));

        queue.Products.Should().Contain(seeded.ProductId);
        await using var after = fixture.CreateContext();
        (await new ProductSearchDocumentLoader(after, TimeProvider.System).LoadAsync([seeded.ProductId])).Single().InStock.Should().BeTrue();
    }

    [PostgresFact]
    public async Task CancelledOrder_GoesBackToTheWarehousesItCameFrom_AndNeverMoreThanWasTaken()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 18);
        var second = await CatalogTestData.AddWarehouseStockAsync(seedDb, seeded.VariantId, 18);
        var order = await ConfirmedOrderAsync(seeded.VariantId, 30); // 18 + 12 across the two warehouses

        RestockRequest Cancel(int quantity) => new(seeded.VariantId, quantity, RestockReason.OrderCancelled, RestockRequest.OrderReference, order);

        var first = await With(s => s.RestockAsync(Cancel(20)));
        first.Lines.Sum(l => l.Quantity).Should().Be(20);
        first.Lines.Should().OnlyContain(l => l.WarehouseId == seeded.WarehouseId || l.WarehouseId == second);

        // A later return against the same order may only put back what is still missing (30 - 20 = 10).
        var tooMuch = () => With(s => s.RestockAsync(Cancel(11) with { Reason = RestockReason.ReturnRestocked }));
        (await tooMuch.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("RESTOCK_EXCEEDS_DEDUCTED");
        await With(s => s.RestockAsync(Cancel(10) with { Reason = RestockReason.ReturnRestocked }));

        await using var db = fixture.CreateContext();
        var balances = await db.InventoryItems.AsNoTracking().Where(i => i.VariantId == seeded.VariantId)
            .ToDictionaryAsync(i => i.WarehouseId, i => (i.OnHand, i.Reserved));
        balances.Should().BeEquivalentTo(new Dictionary<Guid, (int, int)> { [seeded.WarehouseId] = (18, 0), [second] = (18, 0) },
            "every unit went back exactly where it was taken from");
    }

    [PostgresFact]
    public async Task AnOrderThatNeverDeductedStock_CannotRestock_ItsHoldIsReleasedInstead()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 5);
        var pending = Guid.NewGuid();
        (await With(s => s.ReserveAsync(new ReserveStockRequest(pending, [new ReservationLine(seeded.VariantId, 2)])))).Succeeded.Should().BeTrue();

        var act = () => With(s => s.RestockAsync(new RestockRequest(seeded.VariantId, 2, RestockReason.OrderCancelled, RestockRequest.OrderReference, pending)));

        (await act.Should().ThrowAsync<ConflictException>()).Which.Code.Should().Be("RESTOCK_EXCEEDS_DEDUCTED");
        (await With(s => s.CancelAsync(pending))).Should().Be(1);
        var item = await ItemAsync(seeded.InventoryItemId);
        (item.OnHand, item.Reserved).Should().Be((5, 0));
    }

    [PostgresFact]
    public async Task SeveralWarehouses_WithoutAnOrder_RequireAChoice()
    {
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: 1);
        var second = await CatalogTestData.AddWarehouseStockAsync(seedDb, seeded.VariantId, 1);

        var ambiguous = () => With(s => s.RestockAsync(Receipt(seeded.VariantId, 2)));
        await ambiguous.Should().ThrowAsync<ValidationException>();

        (await With(s => s.RestockAsync(Receipt(seeded.VariantId, 2) with { WarehouseId = second }))).Lines.Single().Should()
            .Be(new RestockedLine(second, 2, 3));
    }

    /// <summary>
    /// E7.1 exit test: 50 buyers (reserve, then commit, cancel, or commit and cancel the confirmed order) race 20 restockers
    /// on one item that starts with 3 units. The invariants hold at sampled points and at the end, nothing deadlocks or
    /// fails, and the ledger explains the final balance exactly.
    /// </summary>
    [PostgresFact]
    public async Task ConcurrentRestockAndReserve_NeverNegative_AndTheBalanceAddsUp()
    {
        const int start = 3;
        await using var seedDb = fixture.CreateContext();
        var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand: start);
        var variant = seeded.VariantId;

        using var gate = new ManualResetEventSlim(false);
        using var stop = new CancellationTokenSource();
        var violations = new ConcurrentBag<string>();
        var samples = 0;
        var sampler = Task.Run(async () =>
        {
            while (!stop.IsCancellationRequested)
            {
                var item = await ItemAsync(seeded.InventoryItemId);
                if (!(item.OnHand >= item.Reserved && item.Reserved >= 0)) violations.Add($"{item.OnHand}/{item.Reserved}");
                Interlocked.Increment(ref samples);
                await Task.Delay(3);
            }
        });

        var buyers = Enumerable.Range(0, 50).Select(n => Task.Run(async () =>
        {
            await using var db = fixture.CreateContext();
            var svc = Service(db);
            gate.Wait();
            var order = Guid.NewGuid();
            if (!(await svc.ReserveAsync(new ReserveStockRequest(order, [new ReservationLine(variant, 1)]))).Succeeded) return (Committed: 0, PutBack: 0);
            switch (n % 3)
            {
                case 0:
                    await svc.CommitAsync(order);
                    return (1, 0);
                case 1:
                    await svc.CancelAsync(order);
                    return (0, 0);
                default: // confirmed, then cancelled: the unit comes back
                    await svc.CommitAsync(order);
                    await svc.RestockAsync(new RestockRequest(variant, 1, RestockReason.OrderCancelled, RestockRequest.OrderReference, order));
                    return (1, 1);
            }
        })).ToList();

        var restockers = Enumerable.Range(0, 20).Select(n => Task.Run(async () =>
        {
            await using var db = fixture.CreateContext();
            var svc = Service(db);
            gate.Wait();
            var quantity = 1 + n % 3;
            await svc.RestockAsync(Receipt(variant, quantity));
            return quantity;
        })).ToList();

        gate.Set();
        var bought = await Task.WhenAll(buyers);
        var restocked = await Task.WhenAll(restockers);
        await stop.CancelAsync();
        await sampler;

        violations.Should().BeEmpty();
        samples.Should().BeGreaterThan(0);
        var committed = bought.Sum(b => b.Item1);
        var putBack = bought.Sum(b => b.Item2);
        committed.Should().BeGreaterThanOrEqualTo(1, "at least part of the starting stock sells");

        var final = await ItemAsync(seeded.InventoryItemId);
        final.Reserved.Should().Be(0, "every hold was committed or cancelled");
        final.OnHand.Should().Be(start + restocked.Sum() + putBack - committed);

        await using var verify = fixture.CreateContext();
        var ledger = await verify.StockMovements.AsNoTracking().Where(m => m.InventoryItemId == seeded.InventoryItemId).ToListAsync();
        ledger.Should().HaveCount(20 + putBack);
        ledger.Sum(m => m.Delta).Should().Be(restocked.Sum() + putBack);
    }
}
