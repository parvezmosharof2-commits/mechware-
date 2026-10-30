using System.Collections.Concurrent;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ShopVerse.Application.Inventory;
using ShopVerse.Domain.Inventory;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.IntegrationTests.Support;

namespace ShopVerse.IntegrationTests.Catalog;

/// <summary>
/// Chaos test for the stock engine (brief §8.5): many buyers reserve random multi-line baskets in random
/// line order while others commit, cancel and an expiry sweeper runs — all at once against real PostgreSQL.
/// Afterwards the books must balance exactly: no oversell, no lost or phantom holds, no deadlocks.
/// Runs in its own database because the expiry sweep is global, and alone because it uses up to half
/// of PostgreSQL's connection limit.
/// </summary>
[Collection(SerialDatabaseCollection.Name)]
public class InventoryChaosTests : IClassFixture<PostgresDatabaseFixture>
{
    private const int Variants = 5;
    private const int Buyers = 160;
    private const int Seed = 20261030;

    // PostgreSQL ships with max_connections = 100 (CI too), so cap in-flight buyers below that.
    private const int MaxConcurrentBuyers = 48;

    private readonly PostgresDatabaseFixture _fixture;

    public InventoryChaosTests(PostgresDatabaseFixture fixture) => _fixture = fixture;

    [PostgresFact]
    public async Task ConcurrentReserveCommitCancelAndExpiry_KeepStockBooksExact()
    {
        var rng = new Random(Seed);
        var initial = new Dictionary<Guid, (Guid ItemId, int OnHand)>();
        await using (var seedDb = _fixture.CreateContext())
        {
            for (var i = 0; i < Variants; i++)
            {
                var onHand = rng.Next(15, 40);
                var seeded = await CatalogTestData.SeedVariantAsync(seedDb, onHand);
                initial[seeded.VariantId] = (seeded.InventoryItemId, onHand);
            }
        }

        var variantIds = initial.Keys.ToArray();
        var demand = variantIds.ToDictionary(v => v, _ => 0);

        // Pre-compute every buyer's script so the run is reproducible from the seed.
        var scripts = Enumerable.Range(0, Buyers).Select(_ =>
        {
            var lines = variantIds.OrderBy(_ => rng.Next()).Take(rng.Next(1, 4))
                .Select(v => new ReservationLine(v, rng.Next(1, 5))).ToList();
            foreach (var l in lines) demand[l.VariantId] += l.Quantity;
            var ttl = TimeSpan.FromMinutes(rng.Next(1, 31));
            var action = rng.Next(100) switch { < 45 => "commit", < 75 => "cancel", _ => "hold" };
            return (Lines: lines, Ttl: ttl, Action: action);
        }).ToList();

        demand.Values.Should().OnlyContain(d => d > 0);
        demand.Sum(d => d.Value).Should().BeGreaterThan(initial.Sum(i => i.Value.OnHand) * 2, "the test must create real contention");

        var errors = new ConcurrentBag<Exception>();
        var reserved = 0;
        var shortfalls = 0;
        using var gate = new ManualResetEventSlim(false);
        using var stopSweeper = new CancellationTokenSource();
        using var slots = new SemaphoreSlim(MaxConcurrentBuyers);

        var buyers = scripts.Select(script => Task.Run(async () =>
        {
            gate.Wait();
            await slots.WaitAsync();
            try
            {
                var reference = Guid.NewGuid();
                ReserveStockResult result;
                await using (var db = _fixture.CreateContext())
                {
                    result = await new InventoryService(db, new NoopLockProvider(), TimeProvider.System)
                        .ReserveAsync(new ReserveStockRequest(reference, script.Lines, script.Ttl));
                }

                if (!result.Succeeded)
                {
                    result.Shortfalls.Should().NotBeEmpty("without a distributed lock the only failure mode is insufficient stock");
                    Interlocked.Increment(ref shortfalls);
                    return;
                }

                Interlocked.Increment(ref reserved);
                await using var db2 = _fixture.CreateContext();
                var svc2 = new InventoryService(db2, new NoopLockProvider(), TimeProvider.System);
                if (script.Action == "commit") await svc2.CommitAsync(reference);
                else if (script.Action == "cancel") await svc2.CancelAsync(reference);
            }
            catch (Exception ex)
            {
                errors.Add(ex);
            }
            finally
            {
                slots.Release();
            }
        })).ToList();

        // Expires every hold shorter than 10 minutes, racing the buyers.
        var sweeper = Task.Run(async () =>
        {
            gate.Wait();
            while (!stopSweeper.IsCancellationRequested)
            {
                try
                {
                    await using var db = _fixture.CreateContext();
                    var svc = new InventoryService(db, new NoopLockProvider(), TimeProvider.System);
                    await svc.ReleaseExpiredAsync(DateTime.UtcNow.AddMinutes(10), batchSize: 7);
                }
                catch (Exception ex)
                {
                    errors.Add(ex);
                }
            }
        });

        gate.Set();
        await Task.WhenAll(buyers);
        stopSweeper.Cancel();
        await sweeper;

        // One last sweep after the race, so holds placed during the sweeper's final pass are covered too.
        await using (var finalDb = _fixture.CreateContext())
            await new InventoryService(finalDb, new NoopLockProvider(), TimeProvider.System).ReleaseExpiredAsync(DateTime.UtcNow.AddMinutes(10));

        errors.Should().BeEmpty("no deadlock, serialization or constraint error may escape: {0}", string.Join(" | ", errors.Select(e => e.GetType().Name + ": " + e.Message).Distinct()));
        reserved.Should().BeGreaterThan(0);
        shortfalls.Should().BeGreaterThan(0, "demand exceeds supply, so some buyers must be turned away");

        await using var verify = _fixture.CreateContext();
        var items = await verify.InventoryItems.AsNoTracking().Where(i => variantIds.Contains(i.VariantId)).ToListAsync();
        var reservations = await verify.StockReservations.AsNoTracking().Where(r => variantIds.Contains(r.VariantId)).ToListAsync();

        foreach (var item in items)
        {
            var mine = reservations.Where(r => r.VariantId == item.VariantId).ToList();
            var active = mine.Where(r => r.Status == ReservationStatus.Active).Sum(r => r.Quantity);
            var fulfilled = mine.Where(r => r.Status == ReservationStatus.Fulfilled).Sum(r => r.Quantity);

            item.Reserved.Should().Be(active, "held stock must equal the sum of active reservations");
            item.OnHand.Should().Be(initial[item.VariantId].OnHand - fulfilled, "only fulfilled reservations may consume stock");
            item.Reserved.Should().BeInRange(0, item.OnHand, "never oversell");
            fulfilled.Should().BeLessThanOrEqualTo(initial[item.VariantId].OnHand);
        }

        // Every reservation that was left on hold with a short TTL must have been expired by the sweeper.
        reservations.Where(r => r.Status == ReservationStatus.Active).Should()
            .OnlyContain(r => r.ExpiresAtUtc > DateTime.UtcNow.AddMinutes(9), "the sweeper releases holds expiring within 10 minutes");
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class SerialDatabaseCollection
{
    public const string Name = "Serial database tests";
}
