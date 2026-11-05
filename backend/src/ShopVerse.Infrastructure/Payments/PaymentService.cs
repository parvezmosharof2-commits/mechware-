using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;
using ShopVerse.Application.Inventory;
using ShopVerse.Application.Payments;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Orders;
using ShopVerse.Domain.Payments;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Payments;

public sealed class PaymentService(
    ShopVerseDbContext db, IEnumerable<IPaymentProvider> providers, IInventoryService inventory, TimeProvider clock,
    ILogger<PaymentService>? log = null) : IPaymentService
{
    private readonly ILogger<PaymentService> logger = log ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<PaymentService>.Instance;

    /// <summary>Open payment attempts one order may have at a time (threat model v2, P5).</summary>
    public const int MaxOpenAttemptsPerOrder = 3;

    /// <summary>
    /// An inbox row still marked "received" after this long belongs to an attempt that died without cleaning up (the process
    /// was killed); the next delivery of that event takes it over instead of being answered as a duplicate (threat model v2, P1).
    /// </summary>
    public static readonly TimeSpan StaleClaimAfter = TimeSpan.FromMinutes(2);

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

        // Every attempt is a session the buyer could pay. Keep the number small so one order cannot collect payments without bound.
        var open = await db.Payments.CountAsync(p => p.OrderId == order.Id && p.Status == PaymentStatus.Pending, ct);
        if (open >= MaxOpenAttemptsPerOrder)
            throw new ConflictException("TOO_MANY_PAYMENT_ATTEMPTS", "This order already has open payment attempts. Finish one of them or try again later.");

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
        {
            // The caller learns nothing about which check failed; the reason is for the logs.
            logger.LogWarning("Payment webhook from {Provider} rejected: {Verdict}", provider.Name, verdict);
            throw new UnauthorizedAccessException("Webhook rejected.");
        }

        var parsed = provider.Parse(rawBody) ?? throw new ValidationException("body", "The webhook body is not understood.");

        // The inbox: the unique (provider, event id) index is the only thing that decides who processes this event.
        if (!await ClaimAsync(provider.Name, parsed, now, ct)) return WebhookOutcome.Duplicate;

        WebhookOutcome outcome;
        try
        {
            // Ask the provider directly. What the webhook says about money is never used.
            var reported = await provider.VerifyAsync(parsed.ProviderRef, ct);

            (outcome, var orderId) = await SettleAsync(provider.Name, parsed, reported, now, ct);

            // Turning the hold into a deduction is idempotent, so a retry after a crash between the two steps finishes the job.
            if (outcome is WebhookOutcome.Confirmed or WebhookOutcome.AlreadyConfirmed) await inventory.CommitAsync(orderId, ct);
        }
        catch
        {
            // The event was not handled. Give the claim back, so the provider's retry processes it instead of being told
            // "duplicate" and stopping for good while the buyer's money sits unconfirmed (threat model v2, P1).
            await ReleaseAsync(provider.Name, parsed.EventId);
            throw;
        }

        db.ChangeTracker.Clear();
        var inbox = await db.PaymentEvents.FirstAsync(e => e.Provider == provider.Name && e.EventId == parsed.EventId, ct);
        inbox.Record(outcome.ToString());
        await db.SaveChangesAsync(ct);
        return outcome;
    }

    /// <summary>
    /// Takes the right to process one event: a new inbox row, or a stale one whose earlier attempt died. False means another
    /// attempt has handled the event or is handling it now.
    /// </summary>
    private async Task<bool> ClaimAsync(string provider, ParsedWebhook parsed, DateTime now, CancellationToken ct)
    {
        db.PaymentEvents.Add(new PaymentEvent(provider, parsed.EventId, parsed.ProviderRef, now));
        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
        {
            db.ChangeTracker.Clear();
        }

        // Conditional update: of several deliveries racing for one stale row, exactly one moves its clock forward and wins.
        var staleBefore = now - StaleClaimAfter;
        var taken = await db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE payment_events SET created_at = {now} WHERE provider = {provider} AND event_id = {parsed.EventId} AND outcome = {PaymentEvent.Received} AND created_at < {staleBefore}", ct);
        if (taken == 1) logger.LogWarning("Payment event {EventId} from {Provider} was left unfinished; processing it again.", parsed.EventId, provider);
        return taken == 1;
    }

    private async Task ReleaseAsync(string provider, string eventId)
    {
        db.ChangeTracker.Clear();
        try
        {
            // Not cancellable: the claim must go even when the request that held it was cancelled. If this fails too, the
            // row turns stale and the next delivery takes it over.
            await db.Database.ExecuteSqlInterpolatedAsync(
                $"DELETE FROM payment_events WHERE provider = {provider} AND event_id = {eventId} AND outcome = {PaymentEvent.Received}", CancellationToken.None);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not release payment event {EventId} from {Provider}; it will be retried once stale.", eventId, provider);
        }
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

        // The order was settled by a different payment: two sessions were opened and both were paid. This money is extra and
        // must go back to the buyer, so it is never left looking like an unpaid attempt (threat model v2, P2).
        payment.MarkNeedsRefund("The order had already been paid by another payment.", now);
        await db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        return (WebhookOutcome.NeedsRefund, order.Id);
    }
}
