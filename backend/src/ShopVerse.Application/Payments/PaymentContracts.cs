namespace ShopVerse.Application.Payments;

public sealed record ProviderSession(string ProviderRef, string RedirectUrl);

/// <summary>What the provider itself says about a payment when the server asks it directly. Never taken from a webhook body.</summary>
public sealed record ProviderPayment(bool Paid, long AmountMinor, string Currency);

/// <summary>The only parts of a webhook body the server reads: which event it is and which payment it is about.</summary>
public sealed record ParsedWebhook(string EventId, string ProviderRef);

/// <summary>A payment gateway. Adapters (SSLCommerz, bKash, Stripe, COD, mock) implement this and nothing else touches them.</summary>
public interface IPaymentProvider
{
    string Name { get; }
    string WebhookSecret { get; }
    Task<ProviderSession> CreateSessionAsync(Guid orderId, string orderNumber, long amountMinor, string currency, CancellationToken ct = default);

    /// <summary>Server-to-server query (SSLCommerz Order Validation, bKash query, Stripe retrieve): the source of truth for "was it paid".</summary>
    Task<ProviderPayment> VerifyAsync(string providerRef, CancellationToken ct = default);

    ParsedWebhook? Parse(string rawBody);
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
    AlreadyConfirmed = 6
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
}
