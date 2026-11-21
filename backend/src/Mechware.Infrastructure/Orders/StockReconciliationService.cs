using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Mechware.Application.Inventory;
using Mechware.Application.Orders;
using Mechware.Domain.Identity;
using Mechware.Domain.Inventory;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>Settings of the E7.3 stock sweep (section <c>StockReconciliation</c>).</summary>
public sealed class StockReconciliationOptions
{
    public const string SectionName = "StockReconciliation";
    public TimeSpan Interval { get; init; } = TimeSpan.FromMinutes(10);
    /// <summary>Only orders whose last change is at least this old count, so the sweep never races a request still in flight.</summary>
    public TimeSpan GracePeriod { get; init; } = TimeSpan.FromMinutes(5);
    public int BatchSize { get; init; } = 100;

    public static StockReconciliationOptions From(IConfiguration configuration)
    {
        var d = new StockReconciliationOptions();
        string? Raw(string key) => configuration[$"{SectionName}:{key}"] is { Length: > 0 } raw ? raw : null;
        TimeSpan? Span(string key) => Raw(key) is { } raw ? (TimeSpan.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : TimeSpan.Zero) : null;
        var o = new StockReconciliationOptions
        {
            Interval = Span(nameof(Interval)) ?? d.Interval,
            GracePeriod = Span(nameof(GracePeriod)) ?? d.GracePeriod,
            BatchSize = Raw(nameof(BatchSize)) is { } b ? (int.TryParse(b, out var n) ? n : 0) : d.BatchSize
        };
        if (o.Interval < TimeSpan.FromMinutes(1) || o.GracePeriod < TimeSpan.FromMinutes(1) || o.BatchSize is < 1 or > 1000)
            throw new InvalidOperationException($"{SectionName}: Interval and GracePeriod must be at least 00:01:00, BatchSize 1-1000.");
        return o;
    }
}

/// <summary>One pass: <paramref name="Skipped"/> when another worker holds the sweep lock; orders fixed; orders whose fix failed.</summary>
public sealed record StockReconciliationPass(bool Skipped, int Fixed, int Failed);

/// <summary>
/// E7.3: finishes the stock step of orders a crash (or a failed post-commit step) left half done. Three cases, each idempotent:
/// <list type="bullet">
/// <item><b>commit_hold</b> (A): an order past confirmation with an <c>active</c> hold the confirmation never committed →
/// <see cref="IInventoryService.CommitAsync"/> (never a restock: the stock is still on the shelf, held).</item>
/// <item><b>release_hold</b> (C): a cancelled order with an <c>active</c> hold → <see cref="IInventoryService.CancelAsync"/>.</item>
/// <item><b>return_stock</b> (B): a cancelled order with fulfilled holds of a variant that has no <c>OrderCancelled</c> stock movement →
/// the same post-commit step as the staff cancel (<see cref="OrderCancellationService.ReturnStockAsync"/>), actor system. RestockAsync's
/// key (order, variant, reason) and its cap (never more than the order's holds deducted) make a replay change nothing.</item>
/// <item><b>return_restock</b> (D, F2.3a): an inspected return whose restock did not finish → <see cref="ReturnRequestService.RestockAsync"/>
/// (key (return, variant), the order's cap). Case B does not cover it: B looks only at cancelled orders and OrderCancelled movements.</item>
/// </list>
/// Locks: a session advisory lock (one sweeper at a time; another worker skips the pass), then only the inventory service's own
/// transactions (inventory rows FOR UPDATE in (variant, warehouse) order, then the holds re-read under them). The sweep never locks an
/// order, payment or company row, so it adds no edge to the global order payments → orders → companies → promotions → inventory.
/// A concurrent cancel is safe: commit and release both re-read the active holds under the inventory locks, and the cancel puts back
/// whatever is fulfilled after its release, so a hold is either committed then returned, or released, never both or neither.
/// </summary>
public sealed class StockReconciliationService(MechwareDbContext db, IInventoryService inventory, StockReconciliationOptions options, TimeProvider clock,
    ILogger<StockReconciliationService>? log = null)
{
    public const string AuditAction = "inventory.reconciled";
    /// <summary>The sweep's session advisory lock (per database); nothing else uses this key.</summary>
    public const string LockKey = "hashtextextended('stock-reconciliation', 0)";
    private const string LockSql = $"SELECT pg_try_advisory_lock({LockKey})", UnlockSql = $"SELECT pg_advisory_unlock({LockKey})";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<StockReconciliationPass> RunOnceAsync(DateTime nowUtc, CancellationToken ct = default)
    {
        db.ChangeTracker.Clear();
        await db.Database.OpenConnectionAsync(ct);   // the session lock and every statement below share one database session
        try
        {
            await using (var cmd = db.Database.GetDbConnection().CreateCommand())
            {
                cmd.CommandText = LockSql;
                if (await cmd.ExecuteScalarAsync(ct) is not true) return new StockReconciliationPass(true, 0, 0);
            }
            try { return await SweepAsync(nowUtc, ct); }
            finally
            {
                await db.Database.ExecuteSqlRawAsync(UnlockSql, CancellationToken.None);
            }
        }
        finally { await db.Database.CloseConnectionAsync(); }
    }

    private async Task<StockReconciliationPass> SweepAsync(DateTime nowUtc, CancellationToken ct)
    {
        var cutoff = DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc) - options.GracePeriod;
        var candidates = await db.Orders.AsNoTracking()
            .Where(o => o.Status != OrderStatus.PendingPayment && o.Status != OrderStatus.PendingApproval && (o.UpdatedAtUtc ?? o.PlacedAtUtc) <= cutoff)
            .Select(o => new
            {
                o.Id,
                o.Status,
                Changed = o.UpdatedAtUtc ?? o.PlacedAtUtc,
                Held = db.StockReservations.Any(r => r.ReferenceId == o.Id && r.Status == ReservationStatus.Active),
                Unreturned = o.Status == OrderStatus.Cancelled && db.StockReservations.Any(r => r.ReferenceId == o.Id
                    && r.Status == ReservationStatus.Fulfilled
                    && !db.StockMovements.Any(m => m.ReferenceType == RestockRequest.OrderReference && m.ReferenceId == o.Id
                        && m.Reason == RestockReason.OrderCancelled
                        && db.InventoryItems.Any(i => i.Id == m.InventoryItemId && i.VariantId == r.VariantId)))
            })
            .Where(c => c.Held || c.Unreturned)
            .OrderBy(c => c.Changed).ThenBy(c => c.Id).Take(options.BatchSize).ToListAsync(ct);

        int done = 0, failed = 0;
        foreach (var c in candidates)
        {
            ct.ThrowIfCancellationRequested();
            var step = "";
            var returned = new List<ReturnedStockDto>();
            try
            {
                var fixedAny = false;
                if (c.Status != OrderStatus.Cancelled)
                {
                    step = "commit_hold";
                    var held = await inventory.CommitAsync(c.Id, CancellationToken.None);
                    if (held > 0) fixedAny = await AuditAsync(c.Id, step, new { Holds = held });
                }
                else
                {
                    if (c.Held)
                    {
                        step = "release_hold";
                        var held = await inventory.CancelAsync(c.Id, CancellationToken.None);
                        if (held > 0) fixedAny = await AuditAsync(c.Id, step, new { Holds = held });
                    }
                    if (c.Unreturned)
                    {
                        step = "return_stock";
                        await OrderCancellationService.ReturnStockAsync(db, inventory, c.Id, null, returned);
                        if (returned.Count > 0) fixedAny |= await AuditAsync(c.Id, step, new { Returned = returned });
                    }
                }
                if (fixedAny) done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                db.ChangeTracker.Clear();
                log?.LogError(ex, "Stock reconciliation: order {OrderId}, case {Case} failed; retried on the next pass", c.Id, step);
                // Variants already put back before the failure stay put back (each restock is its own transaction): record them.
                if (returned.Count > 0) await TryAuditAsync(c.Id, "return_stock", new { Returned = returned, Partial = true });
            }
        }

        // return_restock (D, F2.3a): an inspected return with units to restock of a variant that has no ReturnRestocked movement of that
        // return (a crash or a failure after the inspection committed) → the same post-commit step as the inspection, actor system.
        var returns = await db.ReturnRequests.AsNoTracking()
            .Where(r => r.InspectedAtUtc != null && r.InspectedAtUtc <= cutoff && r.Lines.Any(l => l.RestockQuantity > 0
                && !db.StockMovements.Any(m => m.ReferenceType == ReturnRequestService.ReturnReference && m.ReferenceId == r.Id
                    && m.Reason == RestockReason.ReturnRestocked
                    && db.InventoryItems.Any(i => i.Id == m.InventoryItemId && db.OrderItems.Any(o => o.Id == l.OrderItemId && o.VariantId == i.VariantId)))))
            .OrderBy(r => r.InspectedAtUtc).ThenBy(r => r.Id).Select(r => r.Id).Take(options.BatchSize).ToListAsync(ct);
        foreach (var returnId in returns)
        {
            ct.ThrowIfCancellationRequested();
            var restocked = new List<ReturnedStockDto>();
            try
            {
                await ReturnRequestService.RestockAsync(db, inventory, returnId, null, restocked);
                if (restocked.Count > 0 && await AuditAsync(returnId, "return_restock", new { Restocked = restocked }, "return_requests")) done++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failed++;
                db.ChangeTracker.Clear();
                log?.LogError(ex, "Stock reconciliation: return {ReturnId}, case return_restock failed; retried on the next pass", returnId);
                if (restocked.Count > 0) await TryAuditAsync(returnId, "return_restock", new { Restocked = restocked, Partial = true }, "return_requests");
            }
        }
        return new StockReconciliationPass(false, done, failed);
    }

    private async Task TryAuditAsync(Guid orderId, string step, object detail, string entity = "orders")
    {
        try { await AuditAsync(orderId, step, detail, entity); }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            db.ChangeTracker.Clear();
            log?.LogError(ex, "Stock reconciliation: order {OrderId}, the audit row of a partial fix was not written", orderId);
        }
    }

    private async Task<bool> AuditAsync(Guid entityId, string step, object detail, string entity = "orders")
    {
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), null, null, AuditAction, entity, entityId.ToString(), null,
            JsonSerializer.Serialize(new { Case = step, Actor = "system", Detail = detail }, Json), null, clock.GetUtcNow().UtcDateTime));
        await db.SaveChangesAsync(CancellationToken.None);
        db.ChangeTracker.Clear();
        log?.LogWarning("Stock reconciliation: {Entity} {EntityId}, case {Case} fixed", entity, entityId, step);
        return true;
    }
}
