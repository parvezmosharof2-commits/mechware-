using System.Text.Json;
using Mechware.Application.Common;
using Mechware.Domain.Approvals;
using Mechware.Domain.Orders;
using Mechware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mechware.Infrastructure.Notifications;

/// <summary>
/// Outbox consumer that turns domain events into emails: order placed (buyer; order-confirmed) and approval requested
/// (approver of the current step; approval-needed). Recipients and values are read from the database at send time, so
/// the outbox payload carries no personal data. Idempotency and failure handling live in <see cref="NotificationDispatcher"/>.
/// Quote sent: the template exists; wiring waits for D4.
/// </summary>
public sealed class NotificationOutboxPublisher(MechwareDbContext db, NotificationDispatcher dispatcher, StorefrontLinks links) : IOutboxPublisher
{
    public const string OrderConfirmed = "order-confirmed";
    public const string ApprovalNeeded = "approval-needed";

    public Task PublishAsync(OutboxEnvelope message, CancellationToken ct = default) => message.EventType switch
    {
        nameof(OrderPlacedEvent) => OrderPlacedAsync(message, ct),
        nameof(ApprovalRequestedEvent) => ApprovalRequestedAsync(message, ct),
        _ => Task.CompletedTask
    };

    private async Task OrderPlacedAsync(OutboxEnvelope message, CancellationToken ct)
    {
        var orderId = PayloadGuid(message.Payload, "orderId");
        var order = await db.Orders.AsNoTracking().Where(o => o.Id == orderId)
            .Select(o => new { o.OrderNumber, o.UserId, o.GrandTotalMinor, o.Status }).SingleOrDefaultAsync(ct);
        // An order waiting for approval is not confirmed yet; its approver is told instead.
        if (order is null || order.Status == OrderStatus.PendingApproval) return;

        var buyer = await UserAsync(order.UserId, ct);
        if (buyer is null) return;
        var locale = NotificationFormat.Locale(buyer.Locale);
        await dispatcher.DispatchAsync(message.Id, OrderConfirmed,
        [
            new NotificationTarget(buyer.Id, buyer.Email, locale, new Dictionary<string, string>
            {
                ["customerName"] = buyer.FullName,
                ["orderNumber"] = order.OrderNumber,
                ["total"] = NotificationFormat.Money(order.GrandTotalMinor),
                ["orderUrl"] = links.Url(locale, $"account/orders/{Uri.EscapeDataString(order.OrderNumber)}"),
            })
        ], ct);
    }

    private async Task ApprovalRequestedAsync(OutboxEnvelope message, CancellationToken ct)
    {
        var requestId = PayloadGuid(message.Payload, "approvalRequestId");
        var request = await db.ApprovalRequests.AsNoTracking().Include(r => r.Steps).SingleOrDefaultAsync(r => r.Id == requestId, ct);
        // Decided or cancelled before the email went out: nobody needs to be asked any more.
        if (request is null || request.CurrentStep is not { } step || request.SubjectType != "order") return;

        var orderNumber = await db.Orders.AsNoTracking().Where(o => o.Id == request.SubjectId).Select(o => o.OrderNumber).SingleOrDefaultAsync(ct);
        var approver = await UserAsync(step.ApproverUserId, ct);
        var requester = await UserAsync(request.RequesterUserId, ct);
        if (orderNumber is null || approver is null) return;

        var locale = NotificationFormat.Locale(approver.Locale);
        await dispatcher.DispatchAsync(message.Id, ApprovalNeeded,
        [
            new NotificationTarget(approver.Id, approver.Email, locale, new Dictionary<string, string>
            {
                ["approverName"] = approver.FullName,
                ["requesterName"] = requester?.FullName ?? "A colleague",
                ["orderNumber"] = orderNumber,
                ["total"] = NotificationFormat.Money(request.AmountMinor),
                // The approvals tab of the company page (web: account/company/[companyId]); the request is listed there.
                ["approvalUrl"] = links.Url(locale, $"account/company/{request.CompanyId}?tab=approvals"),
            })
        ], ct);
    }

    private Task<Recipient?> UserAsync(Guid userId, CancellationToken ct) =>
        db.Users.AsNoTracking().Where(u => u.Id == userId)
            .Select(u => new Recipient(u.Id, u.Email, u.FullName, u.Locale)).SingleOrDefaultAsync(ct);

    private static Guid PayloadGuid(string payload, string name)
    {
        using var doc = JsonDocument.Parse(payload);
        return doc.RootElement.GetProperty(name).GetGuid();
    }

    private sealed record Recipient(Guid Id, string Email, string FullName, string Locale);
}
