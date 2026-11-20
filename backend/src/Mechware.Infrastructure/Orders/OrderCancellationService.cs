using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Application.Inventory;
using Mechware.Application.Orders;
using Mechware.Domain.Identity;
using Mechware.Domain.Inventory;
using Mechware.Domain.Orders;
using Mechware.Domain.Payments;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// E7.2: staff cancel a confirmed order. One transaction, no outside call inside, locks in the global order (ADR-0010 §7 + B3.5 notes):
/// the order's gateway <c>payments</c> (FOR UPDATE, id order: the payment webhook also takes payment → order) → the <c>orders</c> row
/// (FOR NO KEY UPDATE, never FOR UPDATE: a verification or bounce holding the company inserts ledger lines whose foreign key takes FOR KEY
/// SHARE on it) → <c>companies</c> (inside <see cref="ICreditLedger.ReduceChargeAsync"/>) → <c>promotions</c>. The stock goes back after
/// the commit (<see cref="IInventoryService.RestockAsync"/> opens its own transaction; it is idempotent, so a retry or E7.3 finishes it).
/// </summary>
public sealed class OrderCancellationService(
    MechwareDbContext db, IInventoryService inventory, ICreditLedger credit, ICurrentUserService user, TimeProvider clock,
    ILogger<OrderCancellationService>? log = null) : IOrderCancellationService
{
    public const string CancelledAudit = "order.cancelled_by_staff";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<CancelledOrderDto> CancelConfirmedAsync(Guid orderId, CancelConfirmedOrderRequest request, CancellationToken ct = default)
    {
        var actor = user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        var reason = Order.NormaliseCancelReason(request.Reason);
        var now = clock.GetUtcNow().UtcDateTime;
        // Nothing tracked from earlier in the request: ReduceChargeAsync must read the company fresh under its lock (B3.5 watch item).
        db.ChangeTracker.Clear();

        Order order;
        CreditReductionResult? released = null;
        List<Guid> refunds = [];
        var already = false;
        await using (var tx = await db.Database.BeginTransactionAsync(ct))
        {
            var payments = await db.Payments
                .FromSqlInterpolated($"SELECT p.*, p.xmin FROM payments p WHERE p.order_id = {orderId} ORDER BY p.id FOR UPDATE").ToListAsync(ct);
            order = (await db.Orders.FromSqlInterpolated($"SELECT o.*, o.xmin FROM orders o WHERE o.id = {orderId} FOR NO KEY UPDATE")
                .Include(o => o.SubOrders).ToListAsync(ct)).SingleOrDefault() ?? throw new KeyNotFoundException("Order not found.");

            if (order.Status == OrderStatus.Cancelled) already = true;   // a retry: only the stock return below may still be missing
            else
            {
                var from = order.Status;
                order.CancelAfterConfirmation(actor, reason, now);   // Confirmed, Processing or Packed, else 409
                released = await credit.ReduceChargeAsync(new CreditReduction(order.Id, CreditReductionKind.OrderCancelled, null,
                    $"cancel:{order.Id:N}", null, actor, reason), ct);   // null: not a credit order
                foreach (var payment in payments.Where(p => p.Status == PaymentStatus.Captured))
                {
                    payment.FlagRefundForCancelledOrder(Truncate($"Order cancelled after payment: {reason}", 500), now);
                    refunds.Add(payment.Id);
                }
                await OrderService.ReturnPromotionUsesAsync(db, order.Id, ct);
                db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, user.IpAddress, CancelledAudit, "orders", order.Id.ToString(),
                    JsonSerializer.Serialize(new { Status = from.ToString() }, Json), JsonSerializer.Serialize(new
                    {
                        Status = order.Status.ToString(),
                        order.OrderNumber,
                        order.CompanyId,
                        Reason = reason,
                        CreditReleasedMinor = released?.ReceivableReleasedMinor ?? 0,
                        CreditMovedOnAccountMinor = released?.MovedOnAccountMinor ?? 0,
                        RefundPaymentIds = refunds
                    }, Json), user.CorrelationId, now));
                await db.SaveChangesAsync(ct);   // the order, its event, the outbox event (ConfirmedOrderCancelledEvent), the payments, the audit row
                await tx.CommitAsync(ct);
            }
        }

        // After the commit, and not cancellable by the caller going away: the order is cancelled whatever happens next.
        var (returned, pending) = await ReturnStockAsync(order.Id, actor);
        return new CancelledOrderDto(order.Id, order.OrderNumber, OrderService.ToWire(OrderStatus.Cancelled), already,
            released?.ReceivableReleasedMinor ?? 0, released?.MovedOnAccountMinor ?? 0, refunds, returned, pending);
    }

    /// <summary>
    /// A hold the confirmation never committed (the E7.3 crash window) is released, not restocked: nothing left the shelf. Then every
    /// variant's committed holds go back where they came from, once (RestockAsync is idempotent per order and variant).
    /// </summary>
    private async Task<(List<ReturnedStockDto> Returned, bool Pending)> ReturnStockAsync(Guid orderId, Guid actor)
    {
        var returned = new List<ReturnedStockDto>();
        try
        {
            await ReturnStockAsync(db, inventory, orderId, actor, returned);
            return (returned, false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            log?.LogError(ex, "Order {OrderId} is cancelled but its stock was not returned; cancelling again (or E7.3) finishes it", orderId);
            return (returned, true);
        }
    }

    /// <summary>
    /// The post-commit stock step of a cancelled order, shared with the E7.3 sweep (<see cref="StockReconciliationService"/>, actor
    /// null = system): release any hold never committed, then put each variant's committed holds back once. Lines actually put back
    /// (not replays) are added to <paramref name="returned"/>; it throws on the first failure, leaving the rest for a retry.
    /// </summary>
    internal static async Task ReturnStockAsync(MechwareDbContext db, IInventoryService inventory, Guid orderId, Guid? actor, List<ReturnedStockDto> returned)
    {
        await inventory.CancelAsync(orderId, CancellationToken.None);
        var taken = await db.StockReservations.AsNoTracking().Where(r => r.ReferenceId == orderId && r.Status == ReservationStatus.Fulfilled)
            .GroupBy(r => r.VariantId).Select(g => new { VariantId = g.Key, Quantity = g.Sum(r => r.Quantity) }).ToListAsync(CancellationToken.None);
        foreach (var line in taken.OrderBy(l => l.VariantId))
        {
            var result = await inventory.RestockAsync(new RestockRequest(line.VariantId, line.Quantity, RestockReason.OrderCancelled,
                RestockRequest.OrderReference, orderId, ActorUserId: actor), CancellationToken.None);
            if (!result.AlreadyApplied) returned.Add(new ReturnedStockDto(line.VariantId, line.Quantity));
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}
