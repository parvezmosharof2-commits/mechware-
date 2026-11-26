using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Orders;

/// <summary>
/// F1.1 manual fulfilment. One transaction per move that locks only the <c>orders</c> row (FOR NO KEY UPDATE, like the E7.2 cancel), so
/// it can never form a cycle with the global lock order (payments → orders → companies → promotions): no payment, company or promotion
/// is touched. A parallel cancel waits on the same row lock, and whichever commits second sees the other's status (or a changed
/// <c>xmin</c>) and gets a 409: exactly one wins. Audit row and outbox event (domain-event interceptor) commit with the move.
/// E6.3c: ship issues the Mushak-6.3 in the same transaction, after the order lock and the rowVersion/status checks
/// (<see cref="IInvoiceIssuer"/>; its counter is the last lock). A Live refusal (409 <c>INVOICE_*</c>) rolls the whole ship back, then
/// the refusal is audited in its own transaction.
/// </summary>
public sealed class OrderFulfilmentService(MechwareDbContext db, ICurrentUserService user, TimeProvider clock, IInvoiceIssuer issuer) : IOrderFulfilmentService
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<OrderFulfilmentDto> StartProcessingAsync(Guid orderId, OrderStepRequest request, CancellationToken ct = default) =>
        MoveAsync(orderId, request.RowVersion, "order.processing_started", (o, _, a, now) => { o.StartProcessing(a, now); return null; }, ct);

    public Task<OrderFulfilmentDto> MarkPackedAsync(Guid orderId, OrderStepRequest request, CancellationToken ct = default) =>
        MoveAsync(orderId, request.RowVersion, "order.packed", (o, _, a, now) => { o.MarkPacked(a, now); return null; }, ct);

    public Task<OrderFulfilmentDto> ShipAsync(Guid orderId, ShipOrderRequest request, CancellationToken ct = default)
    {
        // Field errors (400) before anything is locked or looked up.
        Shipment.NormaliseCourierName(request.CourierName);
        Shipment.NormaliseTrackingNumber(request.TrackingNumber);
        Shipment.NormaliseTrackingUrl(request.TrackingUrl);
        return MoveAsync(orderId, request.RowVersion, "order.shipped", (o, existing, a, now) =>
        {
            if (existing.Count > 0) throw new ConflictException("ORDER_ALREADY_SHIPPED", "This order already has a shipment.");
            var shipment = o.Ship(request.CourierName, request.TrackingNumber, request.TrackingUrl, a, now);
            db.Set<Shipment>().Add(shipment);
            return shipment;
        }, ct, issue: true);
    }

    public Task<OrderFulfilmentDto> MarkDeliveredAsync(Guid orderId, OrderStepRequest request, CancellationToken ct = default) =>
        MoveAsync(orderId, request.RowVersion, "order.delivered", (o, existing, a, now) =>
        {
            var shipment = existing.LastOrDefault();
            o.MarkDelivered(shipment, a, now);
            return shipment;
        }, ct);

    private async Task<OrderFulfilmentDto> MoveAsync(
        Guid orderId, uint? rowVersion, string auditAction, Func<Order, IReadOnlyList<Shipment>, Guid, DateTime, Shipment?> move, CancellationToken ct,
        bool issue = false)
    {
        var actor = user.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        if (rowVersion is not { } expected) throw new ValidationException("rowVersion", "Send the rowVersion of the order you loaded.");
        var now = clock.GetUtcNow().UtcDateTime;
        db.ChangeTracker.Clear();

        await using var tx = await db.Database.BeginTransactionAsync(ct);
        var order = (await db.Orders.FromSqlInterpolated($"SELECT o.*, o.xmin FROM orders o WHERE o.id = {orderId} FOR NO KEY UPDATE")
            .Include(o => o.SubOrders).ToListAsync(ct)).SingleOrDefault() ?? throw new KeyNotFoundException("Order not found.");
        if (db.Entry(order).Property<uint>("xmin").CurrentValue != expected)
            throw new ConflictException("ORDER_CHANGED", "Someone changed this order since you loaded it. Reload it and try again.");

        var shipments = await db.Set<Shipment>().Where(s => s.OrderId == orderId).OrderBy(s => s.ShippedAtUtc).ToListAsync(ct);
        var from = order.Status;
        var shipment = move(order, shipments, actor, now);   // 409 ORDER_INVALID_TRANSITION from a wrong status
        db.AuditLogs.Add(new AuditLog(Guid.NewGuid(), actor, user.IpAddress, auditAction, "orders", order.Id.ToString(),
            JsonSerializer.Serialize(new { Status = from.ToString() }, Json), JsonSerializer.Serialize(new
            {
                Status = order.Status.ToString(),
                order.OrderNumber,
                ShipmentId = shipment?.Id,
                shipment?.CourierName,
                shipment?.TrackingNumber,
                shipment?.TrackingUrl
            }, Json), user.CorrelationId, now));
        try
        {
            if (issue && shipment is not null) await issuer.IssueAsync(order, shipment, actor, ct);   // E6.3c, inside this transaction
            await db.SaveChangesAsync(ct);   // the order, its event, the shipment, the outbox event, the audit row
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new ConflictException("ORDER_CHANGED", "Someone changed this order at the same time. Reload it and try again.");
        }
        catch (DbUpdateException e) when (e.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Backstops ux_shipments_order / ux_invoices_one_per_shipment (ADR-0012 §15): a second issue for a shipment is a 409, never a 500.
            throw new ConflictException("ORDER_ALREADY_SHIPPED", "This order already has a shipment.");
        }
        catch (ConflictException refusal) when (IInvoiceIssuer.IsRefusal(refusal))
        {
            await tx.RollbackAsync(CancellationToken.None);   // no shipment, no move, no invoice, no counter step
            await issuer.RecordRefusalAsync(order.Id, order.OrderNumber, refusal, CancellationToken.None);
            throw;
        }
        await tx.CommitAsync(ct);

        if (shipment is not null && !shipments.Contains(shipment)) shipments.Add(shipment);
        return new OrderFulfilmentDto(order.Id, order.OrderNumber, OrderService.ToWire(order.Status), db.Entry(order).Property<uint>("xmin").CurrentValue,
            shipments.Select(ToStaff).ToList(), await db.NumbersOfAsync(order.Id, ct));
    }

    internal static StaffShipmentDto ToStaff(Shipment s) =>
        new(s.Id, s.CourierName, s.TrackingNumber, s.TrackingUrl, s.ShippedAtUtc, s.DeliveredAtUtc, s.CreatedBy);
}
