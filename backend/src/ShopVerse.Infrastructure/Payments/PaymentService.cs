using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopVerse.Application.Inventory;
using ShopVerse.Application.Payments;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Orders;
using ShopVerse.Domain.Payments;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Payments;

public sealed class PaymentService(
    ShopVerseDbContext db, IEnumerable<IPaymentProvider> providers, IInventoryService inventory, TimeProvider clock) : IPaymentService
{
    private IPaymentProvider Provider(string name) =>
        providers.FirstOrDefault(p => string.Equals(p.Name, name, StringComparison.OrdinalIgnoreCase))
        ?? throw new ValidationException("provider", $"Unknown payment provider '{name}'.");

    public async Task<PaymentSessionResult> StartAsync(Guid userId, Guid orderId, string providerName, CancellationToken ct = default)
    {
        var provider = Provider(providerName);
        var order = await db.Orders.AsNoTracking().FirstOrDefaultAsync(o => o.Id == orderId && o.UserId == userId, ct)
                    ?? throw new KeyNotFoundException("Order not found.");
        if (order.Status != OrderStatus.PendingPayment)
            throw new ConflictException("ORDER_NOT_PAYABLE", $"An order that is {order.Status} cannot be paid.");

        // The amount is the order's own total; there is no way for a caller to name one.
        var session = await provider.CreateSessionAsync(order.Id, order.OrderNumber, order.GrandTotalMinor, order.Currency, ct);
        var payment = new Payment(Guid.NewGuid(), order.Id, provider.Name, session.ProviderRef, order.GrandTotalMinor, order.Currency);
        db.Payments.Add(payment);
        await db.SaveChangesAsync(ct);
        return new PaymentSessionResult(payment.Id, provider.Name, session.RedirectUrl, payment.AmountMinor);
    }

    public async Task<WebhookOutcome> HandleWebhookAsync(string providerName, string? signature, string? timestamp, string rawBody, CancellationToken ct = default)
    {
        var provider = Provider(providerName);
        var now = clock.GetUtcNow().UtcDateTime;

        var verdict = WebhookVerifier.Verify(provider.WebhookSecret, signature, timestamp, rawBody, now);
        if (verdict != WebhookVerdict.Accepted)
            throw new UnauthorizedAccessException($"Webhook rejected: {verdict}.");

        var parsed = provider.Parse(rawBody) ?? throw new ValidationException("body", "The webhook body is not understood.");

        // The inbox: the unique (provider, event id) index is the only thing that decides who processes this event.
        db.PaymentEvents.Add(new PaymentEvent(provider.Name, parsed.EventId, parsed.ProviderRef));
        try { await db.SaveChangesAsync(ct); }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
            return WebhookOutcome.Duplicate;
        }

        // Ask the provider directly. What the webhook says about money is never used.
        var reported = await provider.VerifyAsync(parsed.ProviderRef, ct);

        var (outcome, orderId) = await SettleAsync(provider.Name, parsed, reported, now, ct);
        if (outcome == WebhookOutcome.Confirmed) await inventory.CommitAsync(orderId, ct);

        db.ChangeTracker.Clear();
        var inbox = await db.PaymentEvents.FirstAsync(e => e.Provider == provider.Name && e.EventId == parsed.EventId, ct);
        inbox.Record(outcome.ToString());
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    private async Task<(WebhookOutcome Outcome, Guid OrderId)> SettleAsync(
        string provider, ParsedWebhook parsed, ProviderPayment reported, DateTime now, CancellationToken ct)
    {
        db.ChangeTracker.Clear();
        await using var tx = await db.Database.BeginTransactionAsync(ct);

        var payment = (await db.Payments
            .FromSqlInterpolated($"SELECT p.*, p.xmin FROM payments p WHERE p.provider = {provider} AND p.provider_ref = {parsed.ProviderRef} FOR UPDATE")
            .ToListAsync(ct)).SingleOrDefault();
        if (payment is null) return (WebhookOutcome.UnknownPayment, Guid.Empty);

        // Lock the order too, so a cancellation and a payment cannot both win.
        var order = (await db.Orders
            .FromSqlInterpolated($"SELECT o.*, o.xmin FROM orders o WHERE o.id = {payment.OrderId} FOR UPDATE")
            .Include(o => o.SubOrders)
            .ToListAsync(ct)).Single();

        if (payment.Status == PaymentStatus.Captured) return (WebhookOutcome.AlreadyConfirmed, order.Id);

        var paidInFull = reported.Paid && reported.AmountMinor == payment.AmountMinor && reported.Currency == payment.Currency;
        if (!reported.Paid) return (WebhookOutcome.NotPaid, order.Id);   // the customer has not finished; nothing to record

        if (!paidInFull)
        {
            payment.MarkNeedsRefund($"Provider reported {reported.AmountMinor} {reported.Currency}, expected {payment.AmountMinor} {payment.Currency}.", now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (WebhookOutcome.NotPaid, order.Id);
        }

        if (order.Status == OrderStatus.PendingPayment)
        {
            payment.MarkCaptured(now);
            order.Transition(OrderStatus.Confirmed, null, $"Payment captured via {provider}", now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (WebhookOutcome.Confirmed, order.Id);
        }

        if (order.Status == OrderStatus.Cancelled)
        {
            payment.MarkNeedsRefund("Paid after the order was cancelled.", now);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return (WebhookOutcome.NeedsRefund, order.Id);
        }

        return (WebhookOutcome.AlreadyConfirmed, order.Id);
    }
}
