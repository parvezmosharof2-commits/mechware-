using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using ShopVerse.Application.Catalog;
using ShopVerse.Application.Common;
using ShopVerse.Application.Inventory;
using ShopVerse.Domain.Catalog;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Pricing;
using ShopVerse.Domain.Inventory;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Inventory;

/// <summary>
/// Two-phase stock reservation (ADR 0004). Tier 1: optional Redis lock per variant (sorted, so locks
/// are acquired in a fixed order). Tier 2: PostgreSQL <c>SELECT … FOR UPDATE</c> over every affected
/// inventory row in deterministic <c>(variant_id, warehouse_id)</c> order, inside one transaction.
/// Correctness rests on tier 2 and the <c>ck_inventory_available</c> CHECK constraint; tier 1 only
/// reduces contention.
/// </summary>
public sealed class InventoryService : IInventoryService
{
    private static readonly TimeSpan LockExpiry = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan LockWait = TimeSpan.FromSeconds(2);

    private readonly ShopVerseDbContext _db;
    private readonly IDistributedLockProvider _locks;
    private readonly IStorefrontCacheInvalidator? _cache;
    private readonly TimeProvider _clock;

    public InventoryService(
        ShopVerseDbContext db, IDistributedLockProvider locks, TimeProvider clock, IStorefrontCacheInvalidator? cache = null)
    {
        _db = db;
        _locks = locks;
        _clock = clock;
        _cache = cache;
    }

    public async Task<ReserveStockResult> ReserveAsync(ReserveStockRequest request, CancellationToken ct = default)
    {
        if (request.ReferenceId == Guid.Empty) throw new ValidationException("referenceId", "A reference id is required.");
        if (request.Lines.Count == 0) throw new ValidationException("lines", "At least one line is required.");
        if (request.Lines.Count > ReserveStockRequest.MaxLines) throw new ValidationException("lines", $"At most {ReserveStockRequest.MaxLines} lines per reservation.");
        if (request.Lines.Any(l => l.Quantity <= 0 || l.Quantity > ReserveStockRequest.MaxQuantityPerLine))
            throw new ValidationException("quantity", $"Quantity must be between 1 and {ReserveStockRequest.MaxQuantityPerLine}.");

        var lines = request.Lines
            .GroupBy(l => l.VariantId)
            .Select(g => new ReservationLine(g.Key, (int)Math.Min(g.Sum(x => (long)x.Quantity), ReserveStockRequest.MaxQuantityPerLine + 1L)))
            .OrderBy(l => l.VariantId)
            .ToList();
        if (lines.Any(l => l.Quantity > ReserveStockRequest.MaxQuantityPerLine))
            throw new ValidationException("quantity", $"Quantity must be between 1 and {ReserveStockRequest.MaxQuantityPerLine}.");

        var held = new List<IAsyncDisposable>();
        try
        {
            foreach (var line in lines)
            {
                var handle = await _locks.TryAcquireAsync($"lock:inventory:{line.VariantId}", LockExpiry, LockWait, ct);
                if (handle is null) return ReserveStockResult.Busy();
                held.Add(handle);
            }

            var result = await ReserveInTransactionAsync(request, lines, ct);
            if (result.Succeeded) await InvalidateStockAsync(lines.Select(l => l.VariantId), ct);
            return result;
        }
        finally
        {
            foreach (var h in Enumerable.Reverse(held)) await h.DisposeAsync();
        }
    }

    private async Task<ReserveStockResult> ReserveInTransactionAsync(
        ReserveStockRequest request, List<ReservationLine> lines, CancellationToken ct)
    {
        var now = _clock.GetUtcNow().UtcDateTime;
        var ttl = request.Ttl ?? StockReservation.DefaultTtl;
        if (ttl <= TimeSpan.Zero || ttl > ReserveStockRequest.MaxTtl)
            throw new ValidationException("ttl", "Reservation TTL must be between 0 and 30 minutes.");
        var variantIds = lines.Select(l => l.VariantId).ToArray();

        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var items = await LockInventoryAsync(variantIds, ct);

        // Idempotent retry: a reference that already holds stock returns the existing holds, but only if the
        // retry asks for exactly the same lines — otherwise it is a client bug or a tampering attempt.
        var existing = await _db.StockReservations
            .Where(r => r.ReferenceId == request.ReferenceId && r.Status == ReservationStatus.Active)
            .ToListAsync(ct);
        if (existing.Count > 0)
        {
            await tx.RollbackAsync(ct);
            var held = existing.GroupBy(r => r.VariantId).ToDictionary(g => g.Key, g => g.Sum(r => r.Quantity));
            if (held.Count != lines.Count || lines.Any(l => !held.TryGetValue(l.VariantId, out var q) || q != l.Quantity))
                throw new ConflictException("RESERVATION_CONFLICT", "This reference already holds a different reservation.");
            return ReserveStockResult.Ok(existing.Select(r => r.Id).ToList(), existing.Min(r => r.ExpiresAtUtc));
        }

        // Only active variants of active, non-deleted products may be reserved; the price comes from the catalogue.
        var catalogue = await (
            from v in _db.ProductVariants.AsNoTracking()
            join p in _db.Products.AsNoTracking() on v.ProductId equals p.Id
            join c in _db.Categories.AsNoTracking() on p.CategoryId equals c.Id
            where variantIds.Contains(v.Id) && v.Status == VariantStatus.Active && p.Status == ProductStatus.Active && p.DeletedAtUtc == null
            select new { Variant = v, c.VatRate }).ToListAsync(ct);
        var unavailable = variantIds.Where(id => catalogue.All(x => x.Variant.Id != id)).ToList();
        if (unavailable.Count > 0)
        {
            await tx.RollbackAsync(ct);
            return ReserveStockResult.Unavailable(unavailable);
        }

        var unitPrices = catalogue.ToDictionary(
            x => x.Variant.Id,
            x => PricingEngine.Quote(PricingInput.From(x.Variant, x.VatRate), now, TaxMode.Inclusive).Gross.ToMinorUnits());

        var shortfalls = new List<InsufficientStockLine>();
        var plan = new List<(InventoryItem Item, ReservationLine Line)>();
        foreach (var line in lines)
        {
            var candidates = items.Where(i => i.VariantId == line.VariantId).ToList();
            var chosen = candidates
                .Where(i => i.CanReserve(line.Quantity))
                .OrderByDescending(i => i.Available)
                .ThenBy(i => i.WarehouseId)
                .FirstOrDefault();
            if (chosen is null)
            {
                var best = candidates.Count == 0 ? 0 : candidates.Max(i => i.Available);
                shortfalls.Add(new InsufficientStockLine(line.VariantId, line.Quantity, best));
            }
            else
            {
                plan.Add((chosen, line));
            }
        }

        if (shortfalls.Count > 0)
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return ReserveStockResult.Insufficient(shortfalls);
        }

        var ids = new List<Guid>();
        foreach (var (item, line) in plan)
        {
            if (!item.TryReserve(line.Quantity)) throw new InvalidOperationException("Row-locked reservation failed unexpectedly.");
            var reservation = new StockReservation(
                Guid.NewGuid(), line.VariantId, item.WarehouseId, request.ReferenceId, line.Quantity,
                unitPrices[line.VariantId], now, ttl);
            _db.StockReservations.Add(reservation);
            ids.Add(reservation.Id);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _db.ChangeTracker.Clear();
        return ReserveStockResult.Ok(ids, now + ttl);
    }

    public Task<int> CommitAsync(Guid referenceId, CancellationToken ct = default) =>
        SettleAsync(referenceId, commit: true, ct);

    public Task<int> CancelAsync(Guid referenceId, CancellationToken ct = default) =>
        SettleAsync(referenceId, commit: false, ct);

    private async Task<int> SettleAsync(Guid referenceId, bool commit, CancellationToken ct)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var reservations = await _db.StockReservations.AsNoTracking()
            .Where(r => r.ReferenceId == referenceId && r.Status == ReservationStatus.Active)
            .ToListAsync(ct);
        if (reservations.Count == 0)
        {
            await tx.RollbackAsync(ct);
            return 0;
        }

        var variantIds = reservations.Select(r => r.VariantId).Distinct().ToArray();
        var items = await LockInventoryAsync(variantIds, ct);

        // Re-read under the inventory locks so a concurrent sweeper/settle cannot double-apply.
        var active = await _db.StockReservations
            .Where(r => r.ReferenceId == referenceId && r.Status == ReservationStatus.Active)
            .ToListAsync(ct);

        foreach (var r in active)
        {
            var item = items.Single(i => i.VariantId == r.VariantId && i.WarehouseId == r.WarehouseId);
            if (commit)
            {
                item.Commit(r.Quantity);
                r.MarkFulfilled();
            }
            else
            {
                item.Release(r.Quantity);
                r.MarkCancelled();
            }
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _db.ChangeTracker.Clear();
        await InvalidateStockAsync(variantIds, ct);
        return active.Count;
    }

    public async Task<int> ReleaseExpiredAsync(DateTime nowUtc, int batchSize = 500, CancellationToken ct = default)
    {
        // Set-based sweep from ADR 0004, extended to lock inventory rows in deterministic order first.
        // Executed as raw ADO.NET: EF would wrap a query in a sub-select, and PostgreSQL requires
        // data-modifying CTEs to be top-level.
        const string sql = """
            WITH expired_holds AS (
                SELECT id, variant_id, warehouse_id, quantity
                FROM stock_reservations
                WHERE status = 'active' AND expires_at <= @now
                ORDER BY variant_id ASC, warehouse_id ASC
                FOR UPDATE SKIP LOCKED
                LIMIT @batch
            ),
            released_reservations AS (
                UPDATE stock_reservations sr
                SET status = 'expired', updated_at = @now
                FROM expired_holds eh
                WHERE sr.id = eh.id
                RETURNING eh.variant_id, eh.warehouse_id, eh.quantity
            ),
            agg AS (
                SELECT variant_id, warehouse_id, SUM(quantity)::int AS total_qty
                FROM released_reservations
                GROUP BY variant_id, warehouse_id
            ),
            locked_inv AS (
                SELECT i.id, agg.total_qty
                FROM inventory_items i
                JOIN agg ON agg.variant_id = i.variant_id AND agg.warehouse_id = i.warehouse_id
                ORDER BY i.variant_id ASC, i.warehouse_id ASC
                FOR UPDATE OF i
            ),
            updated_inv AS (
                UPDATE inventory_items inv
                SET reserved = inv.reserved - l.total_qty, updated_at = @now
                FROM locked_inv l
                WHERE inv.id = l.id
                RETURNING inv.id
            )
            SELECT (SELECT COUNT(*) FROM released_reservations)::int
            """;

        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        try
        {
            await using var cmd = connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.Transaction = _db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
            cmd.Parameters.AddWithValue("now", NpgsqlTypes.NpgsqlDbType.TimestampTz, DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc));
            cmd.Parameters.AddWithValue("batch", batchSize);
            return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
        }
        finally
        {
            if (openedHere) await connection.CloseAsync();
        }
    }

    /// <summary>Row-locks all warehouse balances of the given variants in deterministic order.</summary>
    private Task<List<InventoryItem>> LockInventoryAsync(Guid[] variantIds, CancellationToken ct) =>
        _db.InventoryItems
            .FromSql($"""
                SELECT i.* FROM inventory_items i
                JOIN warehouses w ON w.id = i.warehouse_id
                WHERE i.variant_id = ANY({variantIds}) AND w.is_active
                ORDER BY i.variant_id ASC, i.warehouse_id ASC
                FOR UPDATE OF i
                """)
            .ToListAsync(ct);

    private async Task InvalidateStockAsync(IEnumerable<Guid> variantIds, CancellationToken ct)
    {
        if (_cache is null) return;
        var ids = variantIds.ToArray();
        var productIds = await _db.ProductVariants.AsNoTracking()
            .Where(v => ids.Contains(v.Id)).Select(v => v.ProductId).Distinct().ToListAsync(ct);
        await _cache.InvalidateStockAsync(productIds, ct);
    }
}
