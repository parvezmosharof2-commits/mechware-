using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Payments;

public enum RefundStatus
{
    /// <summary>A staff member asked for it; a second person must approve before money moves.</summary>
    Requested = 1,

    /// <summary>Approved and sent to the provider; the outcome is not recorded yet.</summary>
    Processing = 2,

    Completed = 3,
    Rejected = 4,

    /// <summary>The provider refused or could not be reached. It can be approved again; the provider sees the same idempotency key.</summary>
    Failed = 5
}

/// <summary>
/// Money going back to a buyer (threat model v2, P3). Four eyes: the person who asks for a refund can never approve it, so no
/// single staff account can send money out. The amount is capped by the payment (checked under a row lock by the service).
/// </summary>
public sealed class Refund : Entity<Guid>
{
    public const int MinReasonLength = 5;
    public const int MaxReasonLength = 500;

    /// <summary>A refund stuck in <see cref="RefundStatus.Processing"/> this long (the process died mid-call) may be approved again.</summary>
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(5);

    public Guid PaymentId { get; private set; }
    public long AmountMinor { get; private set; }
    public string Currency { get; private set; } = "BDT";
    public string Reason { get; private set; } = string.Empty;
    public Guid RequestedBy { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public RefundStatus Status { get; private set; } = RefundStatus.Requested;
    public string? ProviderRefundRef { get; private set; }
    public string? Note { get; private set; }

    private Refund() { }

    public Refund(Guid id, Guid paymentId, long amountMinor, string currency, string reason, Guid requestedBy, DateTime nowUtc) : base(id)
    {
        if (amountMinor <= 0) throw new ValidationException("amount", "A refund must be for a positive amount.");
        var trimmed = (reason ?? string.Empty).Trim();
        if (trimmed.Length is < MinReasonLength or > MaxReasonLength)
            throw new ValidationException("reason", $"Give a reason of {MinReasonLength} to {MaxReasonLength} characters.");
        PaymentId = paymentId;
        AmountMinor = amountMinor;
        Currency = currency;
        Reason = trimmed;
        RequestedBy = requestedBy;
        CreatedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    /// <summary>Counts against the payment: everything except a rejected refund may still move money.</summary>
    public bool HoldsAmount => Status != RefundStatus.Rejected;

    public void Approve(Guid approverId, DateTime nowUtc)
    {
        if (approverId == RequestedBy)
            throw new ConflictException("REFUND_FOUR_EYES", "The person who asked for a refund cannot approve it. Another approver must.");
        var stuck = Status == RefundStatus.Processing && UpdatedAtUtc is { } at && nowUtc - at >= StuckAfter;
        if (Status is not (RefundStatus.Requested or RefundStatus.Failed) && !stuck)
            throw new ConflictException("REFUND_NOT_APPROVABLE", $"A {Status} refund cannot be approved.");
        Status = RefundStatus.Processing;
        DecidedBy = approverId;
        DecidedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }

    public void Complete(string providerRefundRef, DateTime nowUtc)
    {
        if (Status != RefundStatus.Processing) throw new ConflictException("REFUND_NOT_PROCESSING", $"A {Status} refund cannot complete.");
        Status = RefundStatus.Completed;
        ProviderRefundRef = providerRefundRef;
        Note = null;
        UpdatedAtUtc = nowUtc;
    }

    public void Fail(string note, DateTime nowUtc)
    {
        if (Status != RefundStatus.Processing) throw new ConflictException("REFUND_NOT_PROCESSING", $"A {Status} refund cannot fail.");
        Status = RefundStatus.Failed;
        Note = note.Length > MaxReasonLength ? note[..MaxReasonLength] : note;
        UpdatedAtUtc = nowUtc;
    }

    public void Reject(Guid deciderId, string note, DateTime nowUtc)
    {
        if (Status is not (RefundStatus.Requested or RefundStatus.Failed))
            throw new ConflictException("REFUND_NOT_REJECTABLE", $"A {Status} refund cannot be rejected.");
        var trimmed = (note ?? string.Empty).Trim();
        if (trimmed.Length is < MinReasonLength or > MaxReasonLength)
            throw new ValidationException("reason", $"Give a reason of {MinReasonLength} to {MaxReasonLength} characters.");
        Status = RefundStatus.Rejected;
        DecidedBy = deciderId;
        DecidedAtUtc = nowUtc;
        Note = trimmed;
        UpdatedAtUtc = nowUtc;
    }
}
