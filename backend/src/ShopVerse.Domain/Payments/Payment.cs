using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Payments;

public enum PaymentStatus
{
    Pending = 1,
    Captured = 2,
    Failed = 3,

    /// <summary>Money arrived that the order cannot use (it was cancelled meanwhile, or the amount differed): finance must refund.</summary>
    NeedsRefund = 4
}

/// <summary>One attempt to pay an order through one provider. The amount is what the server asked for, never what a client says.</summary>
public sealed class Payment : Entity<Guid>
{
    public Guid OrderId { get; private set; }
    public string Provider { get; private set; } = string.Empty;
    public string ProviderRef { get; private set; } = string.Empty;
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = "BDT";
    public PaymentStatus Status { get; private set; } = PaymentStatus.Pending;
    public string? Note { get; private set; }
    public DateTime? CapturedAtUtc { get; private set; }

    private Payment() { }

    public Payment(Guid id, Guid orderId, string provider, string providerRef, long amountMinor, string currency) : base(id)
    {
        if (amountMinor <= 0) throw new ValidationException("amount", "A payment must be for a positive amount.");
        if (string.IsNullOrWhiteSpace(provider) || string.IsNullOrWhiteSpace(providerRef))
            throw new ValidationException("provider", "Provider and provider reference are required.");
        OrderId = orderId;
        Provider = provider;
        ProviderRef = providerRef;
        AmountMinor = amountMinor;
        Currency = currency;
    }

    public void MarkCaptured(DateTime nowUtc)
    {
        if (Status != PaymentStatus.Pending) throw new ConflictException("PAYMENT_NOT_PENDING", $"A {Status} payment cannot be captured.");
        Status = PaymentStatus.Captured;
        CapturedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkFailed(string note, DateTime nowUtc)
    {
        if (Status != PaymentStatus.Pending) throw new ConflictException("PAYMENT_NOT_PENDING", $"A {Status} payment cannot fail.");
        Status = PaymentStatus.Failed;
        Note = note;
        UpdatedAtUtc = nowUtc;
    }

    public void MarkNeedsRefund(string note, DateTime nowUtc)
    {
        if (Status is PaymentStatus.Captured) throw new ConflictException("PAYMENT_ALREADY_CAPTURED", "A captured payment is already settled.");
        Status = PaymentStatus.NeedsRefund;
        Note = note;
        UpdatedAtUtc = nowUtc;
    }
}

/// <summary>
/// The webhook inbox (brief §8.7): every provider event id is stored once. A second delivery of the same event hits the
/// unique index and is answered as a duplicate without being processed again.
/// </summary>
public sealed class PaymentEvent : Entity<Guid>
{
    public string Provider { get; private set; } = string.Empty;
    public string EventId { get; private set; } = string.Empty;
    public string ProviderRef { get; private set; } = string.Empty;
    public string Outcome { get; private set; } = "received";

    private PaymentEvent() { }

    public PaymentEvent(string provider, string eventId, string providerRef) : base(Guid.NewGuid())
    {
        Provider = provider;
        EventId = eventId;
        ProviderRef = providerRef;
    }

    public void Record(string outcome) => Outcome = outcome;
}
