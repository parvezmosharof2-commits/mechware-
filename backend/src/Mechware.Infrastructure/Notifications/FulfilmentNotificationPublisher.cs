using System.Globalization;
using System.Text.Json;
using Mechware.Application.Common;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Returns;
using Mechware.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace Mechware.Infrastructure.Notifications;

/// <summary>
/// Outbox consumer for the buyer mails after the order is placed (F3b): shipped (courier, tracking number, tracking link when it is a safe
/// https link, challan on the order page), delivered, return approved / rejected (with the reason) / received / money back, and invoice
/// issued (number and a link to the order page; the PDF is never attached). One English staff notice goes to the sales inbox when a return is
/// requested. E-mail only. The recipient is the order's placer (the return's requester), only while that person is an active user; for a company
/// order they must also be an effective member of the company (active, accepted), the D4.12 rule. Free text (the rejection reason, the courier)
/// is only a template value, so the engine encodes it, and it never reaches a subject. Specimen invoices are not mailed.
/// </summary>
public sealed class FulfilmentNotificationPublisher(MechwareDbContext db, NotificationDispatcher dispatcher, StorefrontLinks links, QuoteNoticeOptions options) : IOutboxPublisher
{
    public const string Shipped = "order-shipped";
    public const string Delivered = "order-delivered";
    public const string ReturnApproved = "return-approved";
    public const string ReturnRejected = "return-rejected";
    public const string ReturnReceived = "return-received";
    public const string ReturnMoneyBack = "return-money-back";
    public const string InvoiceIssued = "invoice-issued";
    public const string ReturnStaffNotice = "return-staff-notice";
    public const string InvoiceIssuedEvent = "InvoiceIssuedEvent";
    private const int MaxReason = 500;

    public Task PublishAsync(OutboxEnvelope message, CancellationToken ct = default) => message.EventType switch
    {
        nameof(OrderShippedEvent) => ShippedAsync(message, ct),
        nameof(OrderDeliveredEvent) => OrderMailAsync(message, Delivered, ct),
        nameof(ReturnApprovedEvent) => ReturnMailAsync(message, ReturnApproved, ct),
        nameof(ReturnRejectedEvent) => ReturnMailAsync(message, ReturnRejected, ct),
        nameof(ReturnReceivedEvent) => ReturnMailAsync(message, ReturnReceived, ct),
        nameof(ReturnSettledEvent) => ReturnMailAsync(message, ReturnMoneyBack, ct),
        nameof(ReturnRequestedEvent) => ReturnRequestedAsync(message, ct),
        InvoiceIssuedEvent => InvoiceAsync(message, ct),
        _ => Task.CompletedTask
    };

    private async Task ShippedAsync(OutboxEnvelope message, CancellationToken ct)
    {
        if (await BuyerOfOrderAsync(Id(message, "orderId"), ct) is not { } o) return;
        var tracking = Text(message, "trackingUrl");
        var orderUrl = OrderUrl(o.Buyer.Locale, o.OrderNumber);
        await SendAsync(message.Id, Shipped, o.Buyer, new Dictionary<string, string>
        {
            ["orderNumber"] = o.OrderNumber,
            ["courierName"] = Text(message, "courierName") ?? "",
            ["trackingNumber"] = Text(message, "trackingNumber") ?? "",
            // A tracking link only when it is a safe https link; otherwise the order page, which shows the tracking details.
            ["trackingUrl"] = tracking is not null && EmbeddedTemplates.IsSafeLink(tracking) ? tracking : orderUrl,
            ["orderUrl"] = orderUrl,
        }, ct);
    }

    private async Task OrderMailAsync(OutboxEnvelope message, string template, CancellationToken ct)
    {
        if (await BuyerOfOrderAsync(Id(message, "orderId"), ct) is not { } o) return;
        await SendAsync(message.Id, template, o.Buyer,
            new Dictionary<string, string> { ["orderNumber"] = o.OrderNumber, ["orderUrl"] = OrderUrl(o.Buyer.Locale, o.OrderNumber) }, ct);
    }

    private async Task InvoiceAsync(OutboxEnvelope message, CancellationToken ct)
    {
        if (!string.Equals(Text(message, "mode"), "Live", StringComparison.OrdinalIgnoreCase)) return;
        if (await BuyerOfOrderAsync(Id(message, "orderId"), ct) is not { } o) return;
        await SendAsync(message.Id, InvoiceIssued, o.Buyer, new Dictionary<string, string>
        {
            ["orderNumber"] = o.OrderNumber,
            ["invoiceNumber"] = Text(message, "number") ?? "",
            ["orderUrl"] = OrderUrl(o.Buyer.Locale, o.OrderNumber),
        }, ct);
    }

    private async Task ReturnMailAsync(OutboxEnvelope message, string template, CancellationToken ct)
    {
        var returnId = Id(message, "returnId");
        var ret = await db.ReturnRequests.AsNoTracking().Where(r => r.Id == returnId)
            .Select(r => new { r.OrderId, r.RequestedBy, r.CompanyId, r.RejectionReason, r.Settlement }).SingleOrDefaultAsync(ct);
        if (ret is null) return;
        var number = await db.Orders.AsNoTracking().Where(o => o.Id == ret.OrderId).Select(o => o.OrderNumber).SingleOrDefaultAsync(ct);
        if (number is null || await EffectiveAsync(ret.RequestedBy, ret.CompanyId, ct) is not { } buyer) return;

        var model = new Dictionary<string, string> { ["orderNumber"] = number, ["orderUrl"] = OrderUrl(buyer.Locale, number) };
        if (template == ReturnRejected) model["reason"] = Cut(ret.RejectionReason);
        if (template == ReturnMoneyBack) model["outcome"] = Outcome(buyer.Locale, ret.Settlement);
        await SendAsync(message.Id, template, buyer, model, ct);
    }

    private async Task ReturnRequestedAsync(OutboxEnvelope message, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(options.SalesInboxEmail)) return;
        var inbox = new NotificationTarget(QuoteNotificationPublisher.SalesInboxId, options.SalesInboxEmail.Trim(), "en", new Dictionary<string, string>
        {
            ["staffName"] = "Sales desk",
            ["orderNumber"] = Text(message, "orderNumber") ?? "",
            ["quantity"] = Number(message, "quantity").ToString(CultureInfo.InvariantCulture),
        });
        await dispatcher.DispatchAsync(message.Id, ReturnStaffNotice, [inbox], ct);
    }

    private static string Outcome(string locale, ReturnSettlement? how) => (locale, how) switch
    {
        ("bn", ReturnSettlement.CreditNote) => "আপনার ইনভয়েসের বিপরীতে একটি ক্রেডিট নোট ইস্যু করা হয়েছে।",
        ("bn", ReturnSettlement.Refund) => "টাকা আপনার পেমেন্ট পদ্ধতিতে ফেরত দেওয়া হয়েছে।",
        ("bn", _) => "আপনার ক্রেডিট অ্যাকাউন্টের চার্জ কমানো হয়েছে।",
        (_, ReturnSettlement.CreditNote) => "A credit note was issued against your invoice.",
        (_, ReturnSettlement.Refund) => "The money was refunded to your payment method.",
        _ => "The charge on your credit account was reduced."
    };

    // ---- recipients and reads -------------------------------------------------------------------------------------------------

    private sealed record Person(Guid Id, string Email, string FullName, string Locale);
    private sealed record OrderBuyer(string OrderNumber, Person Buyer);

    private async Task<OrderBuyer?> BuyerOfOrderAsync(Guid orderId, CancellationToken ct)
    {
        var order = await db.Orders.AsNoTracking().Where(o => o.Id == orderId).Select(o => new { o.OrderNumber, o.UserId, o.CompanyId }).SingleOrDefaultAsync(ct);
        return order is null || await EffectiveAsync(order.UserId, order.CompanyId, ct) is not { } p ? null : new OrderBuyer(order.OrderNumber, p);
    }

    /// <summary>The user while active and, for a company order, still an effective member of that company; otherwise nobody.</summary>
    private async Task<Person?> EffectiveAsync(Guid userId, Guid? companyId, CancellationToken ct)
    {
        var u = await db.Users.AsNoTracking().Where(x => x.Id == userId && x.Status == UserStatus.Active)
            .Select(x => new Person(x.Id, x.Email, x.FullName, x.Locale)).SingleOrDefaultAsync(ct);
        if (u is null) return null;
        u = u with { Locale = NotificationFormat.Locale(u.Locale) };
        if (companyId is { } c && !await db.CompanyMembers.AsNoTracking().AnyAsync(m => m.CompanyId == c && m.UserId == u.Id && m.IsActive && m.AcceptedAtUtc != null, ct))
            return null;
        return u;
    }

    private string OrderUrl(string locale, string orderNumber) => links.Url(locale, $"account/orders/{Uri.EscapeDataString(orderNumber)}");

    private Task SendAsync(Guid eventId, string template, Person buyer, Dictionary<string, string> model, CancellationToken ct)
    {
        model["customerName"] = buyer.FullName;
        return dispatcher.DispatchAsync(eventId, template, [new NotificationTarget(buyer.Id, buyer.Email, buyer.Locale, model)], ct);
    }

    private static string Cut(string? s) => s is null ? "" : s.Length <= MaxReason ? s : s[..MaxReason] + "...";

    private static Guid Id(OutboxEnvelope m, string name)
    {
        using var doc = JsonDocument.Parse(m.Payload);
        return doc.RootElement.GetProperty(name).GetGuid();
    }

    private static string? Text(OutboxEnvelope m, string name)
    {
        using var doc = JsonDocument.Parse(m.Payload);
        return doc.RootElement.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    }

    private static int Number(OutboxEnvelope m, string name)
    {
        using var doc = JsonDocument.Parse(m.Payload);
        return doc.RootElement.TryGetProperty(name, out var v) && v.TryGetInt32(out var i) ? i : 0;
    }
}
