namespace Mechware.Application.Payments;

public sealed record ProviderSession(string ProviderRef, string RedirectUrl);

/// <summary>What the provider itself says about a payment when the server asks it directly. Never taken from a webhook body.</summary>
public sealed record ProviderPayment(bool Paid, long AmountMinor, string Currency);

/// <summary>The only parts of a webhook body the server reads: which event it is and which payment it is about.</summary>
public sealed record ParsedWebhook(string EventId, string ProviderRef);

/// <summary>What the provider said about a refund request. <paramref name="RefundRef"/> is set when the money was sent back.</summary>
public sealed record ProviderRefund(bool Succeeded, string? RefundRef, string? Error);

/// <summary>A payment gateway. Adapters (SSLCommerz, bKash, Stripe, COD, mock) implement this and nothing else touches them.</summary>
public interface IPaymentProvider
{
    string Name { get; }
    string WebhookSecret { get; }
    Task<ProviderSession> CreateSessionAsync(Guid orderId, string orderNumber, long amountMinor, string currency, CancellationToken ct = default);

    /// <summary>Server-to-server query (SSLCommerz Order Validation, bKash query, Stripe retrieve): the source of truth for "was it paid".</summary>
    Task<ProviderPayment> VerifyAsync(string providerRef, CancellationToken ct = default);

    ParsedWebhook? Parse(string rawBody);

    /// <summary>
    /// Sends money back for a paid session. <paramref name="idempotencyKey"/> is the same on every retry of one refund, so the
    /// provider pays it out at most once (SSLCommerz refund_ref_id, bKash refund reference).
    /// </summary>
    Task<ProviderRefund> RefundAsync(string providerRef, long amountMinor, string idempotencyKey, CancellationToken ct = default);
}

public sealed record PaymentSessionResult(Guid PaymentId, string Provider, string RedirectUrl, long AmountMinor);

public enum WebhookOutcome
{
    /// <summary>The order is now confirmed.</summary>
    Confirmed = 1,

    /// <summary>This event id was already handled; nothing happened this time.</summary>
    Duplicate = 2,

    /// <summary>The provider did not report the payment as paid, or reported a different amount: nothing was confirmed.</summary>
    NotPaid = 3,

    /// <summary>Paid, but the order is no longer payable (cancelled): the payment is flagged for refund.</summary>
    NeedsRefund = 4,

    /// <summary>A valid event about a payment this server never created.</summary>
    UnknownPayment = 5,

    /// <summary>The order was already confirmed (another event got there first).</summary>
    AlreadyConfirmed = 6,

    /// <summary>The money of this payment was already sent back; a late event changes nothing.</summary>
    AlreadyRefunded = 7
}

public interface IPaymentService
{
    /// <summary>Starts paying a pending order through a provider. The amount is the order's own grand total.</summary>
    Task<PaymentSessionResult> StartAsync(Guid userId, Guid orderId, string provider, CancellationToken ct = default);

    /// <summary>
    /// Handles a provider webhook: verifies the signature and freshness, records the event id once, asks the provider
    /// directly whether the payment was made for the right amount, and only then confirms the order.
    /// </summary>
    Task<WebhookOutcome> HandleWebhookAsync(string provider, string? signature, string? timestamp, string rawBody, CancellationToken ct = default);

    /// <summary>
    /// Asks the provider directly about payment attempts whose webhook has not arrived (threat model v2, P4) and settles any it
    /// reports as paid, exactly as a webhook would. Returns how many orders it confirmed.
    /// </summary>
    Task<int> ReconcileAsync(DateTime nowUtc, int batch, CancellationToken ct = default);
}

public sealed record RefundDto(
    Guid Id, Guid PaymentId, long AmountMinor, string Currency, string Reason, string Status, Guid RequestedBy, Guid? DecidedBy,
    DateTime RequestedAtUtc, DateTime? DecidedAtUtc, string? ProviderRefundRef, string? Note);

/// <summary>A payment holding money that has to go back, with what has been asked for and done about it so far.</summary>
public sealed record RefundQueueItem(
    Guid PaymentId, Guid OrderId, string OrderNumber, string Provider, long PaidMinor, long RefundableMinor, string Currency, string? Note,
    IReadOnlyList<RefundDto> Refunds);

public sealed record RequestRefund(long AmountMinor, string Reason);
public sealed record RejectRefund(string Reason);

/// <summary>
/// Refunds with four eyes (threat model v2, P3): one staff member asks, a different one approves, and only then does the
/// provider move money. The amount can never exceed what the payment still holds.
/// </summary>
public interface IRefundService
{
    /// <summary>Payments flagged NeedsRefund, and every payment with a refund that is not finished yet.</summary>
    Task<IReadOnlyList<RefundQueueItem>> QueueAsync(CancellationToken ct = default);

    Task<RefundDto> RequestAsync(Guid actorId, Guid paymentId, RequestRefund request, CancellationToken ct = default);

    /// <summary>Approves and executes. A provider failure leaves the refund Failed (approve again to retry), never half-recorded.</summary>
    Task<RefundDto> ApproveAsync(Guid actorId, Guid refundId, CancellationToken ct = default);

    Task<RefundDto> RejectAsync(Guid actorId, Guid refundId, RejectRefund request, CancellationToken ct = default);
}
