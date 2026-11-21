using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;
using Mechware.Application.Catalog;
using Mechware.Application.Common;
using Mechware.Application.Identity;
using Mechware.Application.Inventory;
using Mechware.Application.Search;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing;
using Mechware.Domain.Inventory;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Inventory;

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

    private readonly MechwareDbContext _db;
    private readonly IDistributedLockProvider _locks;
    private readonly IStorefrontCacheInvalidator? _cache;
    private readonly TimeProvider _clock;
    private readonly ISearchReindexQueue? _reindex;
    private readonly ICurrentUserService? _user;

    public InventoryService(
        MechwareDbContext db, IDistributedLockProvider locks, TimeProvider clock, IStorefrontCacheInvalidator? cache = null,
        ISearchReindexQueue? reindex = null, ICurrentUserService? user = null)
    {
        _reindex = reindex;
        _user = user;
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
            throw new ValidationException("ttl", $"Reservation TTL must be between 0 and {ReserveStockRequest.MaxTtl.TotalHours} hours.");
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
        var plan = new List<(InventoryItem Item, ReservationLine Line, int Quantity)>();
        foreach (var line in lines)
        {
            var allocation = Allocate(items.Where(i => i.VariantId == line.VariantId).ToList(), line.Quantity);
            if (allocation is null)
            {
                var total = items.Where(i => i.VariantId == line.VariantId).Sum(i => Math.Max(i.Available, 0));
                shortfalls.Add(new InsufficientStockLine(line.VariantId, line.Quantity, total));
            }
            else
            {
                plan.AddRange(allocation.Select(a => (a.Item, line, a.Quantity)));
            }
        }

        if (shortfalls.Count > 0)
        {
            await tx.RollbackAsync(ct);
            _db.ChangeTracker.Clear();
            return ReserveStockResult.Insufficient(shortfalls);
        }

        var ids = new List<Guid>();
        foreach (var (item, line, quantity) in plan)
        {
            if (!item.TryReserve(quantity)) throw new InvalidOperationException("Row-locked reservation failed unexpectedly.");
            var reservation = new StockReservation(
                Guid.NewGuid(), line.VariantId, item.WarehouseId, request.ReferenceId, quantity,
                unitPrices[line.VariantId], now, ttl);
            _db.StockReservations.Add(reservation);
            ids.Add(reservation.Id);
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _db.ChangeTracker.Clear();
        return ReserveStockResult.Ok(ids, now + ttl);
    }

    /// <summary>
    /// Decides which warehouses a line is held in: largest balance first, then the lowest warehouse id. A warehouse that can
    /// supply the whole line therefore takes all of it (one parcel); only when none can is the line split, so the buyer can
    /// have everything the storefront shows as available across active warehouses (finding #3 of the 2026-11-04 test: the
    /// storefront summed every warehouse but checkout only ever used one). Null when they do not have enough together.
    /// </summary>
    internal static List<(InventoryItem Item, int Quantity)>? Allocate(IReadOnlyList<InventoryItem> balances, int quantity)
    {
        var allocation = new List<(InventoryItem Item, int Quantity)>();
        var remaining = quantity;
        foreach (var item in balances.Where(i => i.Available > 0).OrderByDescending(i => i.Available).ThenBy(i => i.WarehouseId))
        {
            var take = Math.Min(item.Available, remaining);
            allocation.Add((item, take));
            remaining -= take;
            if (remaining == 0) return allocation;
        }

        return null;
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
        // Lock order must match reserve/commit/cancel: inventory rows first, then reservation rows. Locking
        // reservations first (as a single CTE would) deadlocks against a concurrent commit (found by the
        // inventory chaos test). The sweeper uses SKIP LOCKED on inventory, so it never waits on checkout
        // traffic: busy balances are simply retried on the next sweep.
        const string lockSql = """
            SELECT i.variant_id, i.warehouse_id
            FROM inventory_items i
            WHERE (i.variant_id, i.warehouse_id) IN (
                SELECT variant_id, warehouse_id
                FROM stock_reservations
                WHERE status = 'active' AND expires_at <= @now
                ORDER BY expires_at
                LIMIT @batch)
            ORDER BY i.variant_id ASC, i.warehouse_id ASC
            FOR UPDATE OF i SKIP LOCKED
            """;

        // Executed as raw ADO.NET: EF would wrap a query in a sub-select, and PostgreSQL requires
        // data-modifying CTEs to be top-level.
        const string releaseSql = """
            WITH expired_holds AS (
                SELECT sr.id, sr.variant_id, sr.warehouse_id, sr.quantity
                FROM stock_reservations sr
                JOIN UNNEST(@variants, @warehouses) AS locked(variant_id, warehouse_id)
                  ON locked.variant_id = sr.variant_id AND locked.warehouse_id = sr.warehouse_id
                WHERE sr.status = 'active' AND sr.expires_at <= @now
                ORDER BY sr.expires_at
                LIMIT @batch
                FOR UPDATE OF sr
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
            updated_inv AS (
                UPDATE inventory_items inv
                SET reserved = inv.reserved - agg.total_qty, updated_at = @now
                FROM agg
                WHERE inv.variant_id = agg.variant_id AND inv.warehouse_id = agg.warehouse_id
                RETURNING inv.id
            )
            SELECT (SELECT COUNT(*) FROM released_reservations)::int
            """;

        var now = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc);
        var connection = (NpgsqlConnection)_db.Database.GetDbConnection();
        var openedHere = connection.State != System.Data.ConnectionState.Open;
        if (openedHere) await connection.OpenAsync(ct);
        var ambient = _db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction;
        var tx = ambient ?? await connection.BeginTransactionAsync(ct);
        try
        {
            var variants = new List<Guid>();
            var warehouses = new List<Guid>();
            await using (var lockCmd = new NpgsqlCommand(lockSql, connection, tx))
            {
                lockCmd.Parameters.AddWithValue("now", NpgsqlTypes.NpgsqlDbType.TimestampTz, now);
                lockCmd.Parameters.AddWithValue("batch", batchSize);
                await using var reader = await lockCmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    variants.Add(reader.GetGuid(0));
                    warehouses.Add(reader.GetGuid(1));
                }
            }

            var released = 0;
            if (variants.Count > 0)
            {
                await using var cmd = new NpgsqlCommand(releaseSql, connection, tx);
                cmd.Parameters.AddWithValue("now", NpgsqlTypes.NpgsqlDbType.TimestampTz, now);
                cmd.Parameters.AddWithValue("batch", batchSize);
                cmd.Parameters.AddWithValue("variants", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, variants.ToArray());
                cmd.Parameters.AddWithValue("warehouses", NpgsqlTypes.NpgsqlDbType.Array | NpgsqlTypes.NpgsqlDbType.Uuid, warehouses.ToArray());
                released = Convert.ToInt32(await cmd.ExecuteScalarAsync(ct));
            }

            if (ambient is null) await tx.CommitAsync(ct);
            if (ambient is null && released > 0) await InvalidateStockAsync(variants, ct);
            return released;
        }
        catch
        {
            if (ambient is null) await tx.RollbackAsync(CancellationToken.None);
            throw;
        }
        finally
        {
            if (ambient is null) await tx.DisposeAsync();
            if (openedHere) await connection.CloseAsync();
        }
    }

    public async Task<RestockResult> RestockAsync(RestockRequest request, CancellationToken ct = default)
    {
        ValidateRestock(request);
        if (!await _db.ProductVariants.AsNoTracking().AnyAsync(v => v.Id == request.VariantId, ct))
            throw new KeyNotFoundException("Variant not found.");
        var now = _clock.GetUtcNow().UtcDateTime;

        List<RestockedLine> lines;
        await using (var tx = await _db.Database.BeginTransactionAsync(ct))
        {
            // The lock discipline of reserve/commit/cancel: every balance of the variant FOR UPDATE in (variant, warehouse)
            // order. Inactive warehouses are included (stock can go back to a warehouse closed for sale); the order is the same.
            // No Redis lock, like commit/cancel: tier 1 only reduces contention, and a restock must not fail as "busy".
            var items = await LockInventoryAsync([request.VariantId], ct, includeInactive: true);
            var itemIds = items.Select(i => i.Id).ToArray();

            // Idempotent replay, checked under the locks so two copies of one restock cannot both pass.
            var replay = await _db.StockMovements.AsNoTracking()
                .Where(m => m.ReferenceType == request.ReferenceType && m.ReferenceId == request.ReferenceId
                    && m.Reason == request.Reason && itemIds.Contains(m.InventoryItemId))
                .ToListAsync(ct);
            if (replay.Count > 0)
            {
                _db.ChangeTracker.Clear();
                if (replay.Sum(m => m.Delta) != request.Quantity)
                    throw new ConflictException("RESTOCK_CONFLICT", "This reference already restocked a different quantity of this item.");
                var byId = items.ToDictionary(i => i.Id);
                return new RestockResult(true, replay
                    .Select(m => new RestockedLine(byId[m.InventoryItemId].WarehouseId, m.Delta, byId[m.InventoryItemId].OnHand)).ToList());
            }

            lines = [];
            var againstOrder = request.ReferenceType == RestockRequest.OrderReference ? request.ReferenceId : request.AgainstOrderId;
            foreach (var (item, quantity) in await PlanRestockAsync(request, againstOrder, items, ct))
            {
                item.Restock(quantity);
                _db.StockMovements.Add(new StockMovement(Guid.NewGuid(), item.Id, quantity, request.Reason, request.ReferenceType,
                    request.ReferenceId, request.ActorUserId, now, againstOrder));
                lines.Add(new RestockedLine(item.WarehouseId, quantity, item.OnHand));
            }

            _db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), request.ActorUserId, _user?.IpAddress, "inventory.restocked", "product_variants",
                request.VariantId.ToString(), null, JsonSerializer.Serialize(new
                {
                    request.VariantId,
                    request.Quantity,
                    Reason = request.Reason.ToString(),
                    request.ReferenceType,
                    request.ReferenceId,
                    request.AgainstOrderId,
                    Actor = request.ActorUserId?.ToString() ?? "system",
                    Lines = lines
                }), _user?.CorrelationId, now));
            await _db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            _db.ChangeTracker.Clear();
        }

        // Same freshness path as reserve/commit: storefront stock cache and the search index (out of stock → in stock).
        await InvalidateStockAsync([request.VariantId], ct);
        return new RestockResult(false, lines);
    }

    private static void ValidateRestock(RestockRequest r)
    {
        if (r.VariantId == Guid.Empty) throw new ValidationException("variantId", "A variant is required.");
        if (r.Quantity <= 0 || r.Quantity > RestockRequest.MaxQuantity)
            throw new ValidationException("quantity", $"Quantity must be between 1 and {RestockRequest.MaxQuantity}.");
        if (!Enum.IsDefined(r.Reason))
            throw new ValidationException("reason", "Choose a reason: OrderCancelled, ReturnRestocked, Correction or StockReceived.");
        if (string.IsNullOrEmpty(r.ReferenceType) || r.ReferenceType.Length > RestockRequest.MaxReferenceTypeLength
            || !r.ReferenceType.All(c => c is (>= 'a' and <= 'z') or '_'))
            throw new ValidationException("referenceType", $"A reference type of 1 to {RestockRequest.MaxReferenceTypeLength} lowercase letters or underscores is required.");
        if (r.ReferenceId == Guid.Empty) throw new ValidationException("referenceId", "A reference id is required.");
        if (r.Reason == RestockReason.OrderCancelled && r.ReferenceType != RestockRequest.OrderReference)
            throw new ValidationException("referenceType", "Stock of a cancelled order is put back against that order.");
        if (r.WarehouseId == Guid.Empty) throw new ValidationException("warehouseId", "Leave the warehouse out or name a real one.");
        if (r.AgainstOrderId == Guid.Empty
            || (r.AgainstOrderId is { } order && r.ReferenceType == RestockRequest.OrderReference && order != r.ReferenceId))
            throw new ValidationException("againstOrderId", "Leave the order out or name the order whose stock goes back.");
    }

    /// <summary>Which balances receive the stock; see the warehouse rule on <see cref="RestockRequest"/>.</summary>
    private async Task<List<(InventoryItem Item, int Quantity)>> PlanRestockAsync(
        RestockRequest request, Guid? againstOrder, List<InventoryItem> items, CancellationToken ct)
    {
        if (request.WarehouseId is { } warehouse) items = items.Where(i => i.WarehouseId == warehouse).ToList();
        if (items.Count == 0) throw new KeyNotFoundException("This variant has no stock record in that warehouse.");

        if (againstOrder is not { } orderId)
        {
            if (items.Count == 1) return [(items[0], request.Quantity)];
            var ids = items.Select(i => i.WarehouseId).ToArray();
            var active = await _db.Warehouses.AsNoTracking().Where(w => ids.Contains(w.Id) && w.IsActive).Select(w => w.Id).ToListAsync(ct);
            var candidates = items.Where(i => active.Contains(i.WarehouseId)).ToList();
            if (candidates.Count != 1)
                throw new ValidationException("warehouseId", "This variant is stocked in several warehouses (or none is active); choose one.");
            return [(candidates[0], request.Quantity)];
        }

        // An order's stock goes back where its committed holds took it from, and never more than they took minus what was
        // already put back against this order (any reason, any reference: a cancel or every return). Reservations are never
        // deleted, so they are the deduction record. Read under the balance locks, so parallel returns cannot both pass.
        var deducted = await _db.StockReservations.AsNoTracking()
            .Where(r => r.ReferenceId == orderId && r.VariantId == request.VariantId && r.Status == ReservationStatus.Fulfilled)
            .GroupBy(r => r.WarehouseId).Select(g => new { g.Key, Quantity = g.Sum(r => r.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Quantity, ct);
        var itemIds = items.Select(i => i.Id).ToArray();
        var returned = await _db.StockMovements.AsNoTracking()
            .Where(m => ((m.ReferenceType == RestockRequest.OrderReference && m.ReferenceId == orderId) || m.OrderId == orderId)
                && itemIds.Contains(m.InventoryItemId))
            .GroupBy(m => m.InventoryItemId).Select(g => new { g.Key, Delta = g.Sum(m => m.Delta) })
            .ToDictionaryAsync(x => x.Key, x => x.Delta, ct);

        var open = items
            .Select(i => (Item: i, Left: deducted.GetValueOrDefault(i.WarehouseId) - returned.GetValueOrDefault(i.Id)))
            .Where(x => x.Left > 0).OrderByDescending(x => x.Left).ThenBy(x => x.Item.WarehouseId).ToList();
        var left = open.Sum(x => x.Left);
        if (left < request.Quantity)
            throw new ConflictException("RESTOCK_EXCEEDS_DEDUCTED", $"Only {left} of this item were taken from stock for this order and not yet put back.");

        var plan = new List<(InventoryItem Item, int Quantity)>();
        var remaining = request.Quantity;
        foreach (var (item, quantity) in open)
        {
            var take = Math.Min(quantity, remaining);
            plan.Add((item, take));
            remaining -= take;
            if (remaining == 0) break;
        }

        return plan;
    }

    /// <summary>Row-locks all warehouse balances of the given variants in deterministic order.</summary>
    private Task<List<InventoryItem>> LockInventoryAsync(Guid[] variantIds, CancellationToken ct, bool includeInactive = false) =>
        _db.InventoryItems
            .FromSql($"""
                SELECT i.* FROM inventory_items i
                JOIN warehouses w ON w.id = i.warehouse_id
                WHERE i.variant_id = ANY({variantIds}) AND (w.is_active OR {includeInactive})
                ORDER BY i.variant_id ASC, i.warehouse_id ASC
                FOR UPDATE OF i
                """)
            .ToListAsync(ct);

    private async Task InvalidateStockAsync(IEnumerable<Guid> variantIds, CancellationToken ct)
    {
        if (_cache is null && _reindex is null) return;
        var ids = variantIds.ToArray();
        var productIds = await _db.ProductVariants.AsNoTracking()
            .Where(v => ids.Contains(v.Id)).Select(v => v.ProductId).Distinct().ToListAsync(ct);
        if (_cache is not null) await _cache.InvalidateStockAsync(productIds, ct);
        if (_reindex is not null) await _reindex.EnqueueProductsAsync(productIds, ct);
    }
}
