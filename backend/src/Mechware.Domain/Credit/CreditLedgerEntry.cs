using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Credit;

/// <summary>What a ledger line is (ADR-0010 §2.2). Stored as snake_case text; starts at 1 so a missing value is never valid.</summary>
public enum CreditLedgerKind
{
    Charge = 1, PaymentApplied = 2, PaymentOnAccount = 3, OnAccountApplied = 4, PaymentReversed = 5,
    ChargeReduced = 6, WriteOff = 7, OnAccountCorrection = 8, OnAccountPayout = 9
}

/// <summary>
/// One line a business event wants to post, before it is numbered: the kind, the two signed amounts in whole poisha
/// (R = receivable, A = on-account) and what the line is about. <see cref="IdempotencyKey"/> is deterministic per line.
/// </summary>
public sealed record CreditPosting(
    CreditLedgerKind Kind, long ReceivableDeltaMinor, long OnAccountDeltaMinor, string IdempotencyKey, DateOnly OccurredOn,
    Guid? OrderId = null, Guid? PaymentId = null, Guid? AdjustmentId = null, Guid? CreditNoteId = null, DateOnly? DueOn = null,
    Guid? EventId = null, Guid? ActorUserId = null, string? CorrelationId = null, string? Reason = null);

/// <summary>Deterministic ledger keys (ADR-0010 §2.1): a replayed command finds its own line instead of writing a second one.</summary>
public static class CreditLedgerKeys
{
    public const int MaxLength = 120;
    public static string Charge(Guid orderId) => $"charge:{orderId:N}";
}

/// <summary>Credit dates are Bangladesh calendar dates: a fixed +06:00 (no daylight saving), as for E8.3.</summary>
public static class CreditCalendar
{
    public static DateOnly DhakaDate(DateTime utc) =>
        DateOnly.FromDateTime(DateTime.SpecifyKind(utc, DateTimeKind.Utc).AddHours(6));
}

/// <summary>
/// One append-only line of a company's credit ledger (ADR-0010 §2): signed receivable and on-account amounts with the running
/// totals after the line, numbered 1, 2, 3 … per company. Created only by <see cref="Companies.Company.PostCredit"/>, which
/// also moves the company's cached totals; the database refuses UPDATE, DELETE and TRUNCATE, a broken chain, and cached
/// totals that differ from the last line at commit (CreditLedgerSql).
/// </summary>
public sealed class CreditLedgerEntry : Entity<Guid>
{
    public const string BdtCurrency = "BDT";
    public const int MaxReasonLength = 500;
    public const int MaxCorrelationIdLength = 100;

    public Guid CompanyId { get; private set; }
    public long Seq { get; private set; }
    public Guid EventId { get; private set; }
    public CreditLedgerKind Kind { get; private set; }
    public Guid? OrderId { get; private set; }
    public Guid? PaymentId { get; private set; }
    public Guid? AdjustmentId { get; private set; }
    public Guid? CreditNoteId { get; private set; }
    public long ReceivableDeltaMinor { get; private set; }
    public long OnAccountDeltaMinor { get; private set; }
    public long ReceivableAfterMinor { get; private set; }
    public long OnAccountAfterMinor { get; private set; }
    /// <summary>Charges only: the Dhaka date the order is due, fixed when it is charged (§3).</summary>
    public DateOnly? DueOn { get; private set; }
    public DateOnly OccurredOn { get; private set; }
    public string Currency { get; private set; } = BdtCurrency;
    public string IdempotencyKey { get; private set; } = string.Empty;
    /// <summary>Null when the system did it.</summary>
    public Guid? ActorUserId { get; private set; }
    public string? CorrelationId { get; private set; }
    public string? Reason { get; private set; }

    private CreditLedgerEntry() { }

    internal CreditLedgerEntry(Guid companyId, long seq, CreditPosting p, long receivableAfter, long onAccountAfter, DateTime nowUtc)
        : base(Guid.NewGuid())
    {
        CompanyId = companyId;
        Seq = seq;
        EventId = p.EventId ?? Guid.NewGuid();
        Kind = p.Kind;
        OrderId = p.OrderId;
        PaymentId = p.PaymentId;
        AdjustmentId = p.AdjustmentId;
        CreditNoteId = p.CreditNoteId;
        ReceivableDeltaMinor = p.ReceivableDeltaMinor;
        OnAccountDeltaMinor = p.OnAccountDeltaMinor;
        ReceivableAfterMinor = receivableAfter;
        OnAccountAfterMinor = onAccountAfter;
        DueOn = p.DueOn;
        OccurredOn = p.OccurredOn;
        IdempotencyKey = p.IdempotencyKey;
        ActorUserId = p.ActorUserId;
        CorrelationId = p.CorrelationId;
        Reason = p.Reason;
        CreatedAtUtc = nowUtc;
    }

    /// <summary>
    /// The sign and reference rules of §2.2, the same as the database CHECKs: a receivable rises only through a charge or the
    /// reversal of a payment, always for an order, never through an adjustment; on-account money always names its source.
    /// </summary>
    public static void Validate(CreditPosting p)
    {
        long r = p.ReceivableDeltaMinor, a = p.OnAccountDeltaMinor;
        bool order = p.OrderId is not null, payment = p.PaymentId is not null, adjustment = p.AdjustmentId is not null;
        bool source = payment || p.CreditNoteId is not null;
        bool ok = p.Kind switch
        {
            CreditLedgerKind.Charge => r > 0 && a == 0 && order && p.DueOn is not null,
            CreditLedgerKind.PaymentApplied => r < 0 && a == 0 && order && payment,
            CreditLedgerKind.PaymentOnAccount => r == 0 && a > 0 && payment,
            CreditLedgerKind.OnAccountApplied => r < 0 && a == r && order && source,
            CreditLedgerKind.PaymentReversed => payment && ((r > 0 && a == 0 && order) || (r == 0 && a < 0)),
            CreditLedgerKind.ChargeReduced => order && ((r < 0 && a == 0) || (r == 0 && a > 0 && source)),
            CreditLedgerKind.WriteOff => r < 0 && a == 0 && order && adjustment,
            CreditLedgerKind.OnAccountCorrection => r == 0 && a != 0 && adjustment && source,
            CreditLedgerKind.OnAccountPayout => r == 0 && a < 0 && adjustment && source,
            _ => false
        };
        if (!ok || (r > 0 && adjustment) || (r != 0 && !order))
            throw new ValidationException("kind", $"A {p.Kind} line cannot have these amounts or references.");
        if (p.DueOn is not null && p.Kind != CreditLedgerKind.Charge)
            throw new ValidationException("dueOn", "Only a charge has a due date.");
        if (p.IdempotencyKey is not { Length: > 0 and <= CreditLedgerKeys.MaxLength } || p.IdempotencyKey.Any(c => c is <= ' ' or > '~'))
            throw new ValidationException("idempotencyKey", $"A ledger key is 1 to {CreditLedgerKeys.MaxLength} printable ASCII characters.");
        if (p.Reason is { Length: > MaxReasonLength }) throw new ValidationException("reason", $"A reason is at most {MaxReasonLength} characters.");
        if (p.CorrelationId is { Length: > MaxCorrelationIdLength }) throw new ValidationException("correlationId", "The correlation id is too long.");
    }

    /// <summary>Whether a replay with this key asks for exactly this line (then it is a no-op) or for something else (a conflict).</summary>
    public bool Matches(CreditPosting p) =>
        Kind == p.Kind && ReceivableDeltaMinor == p.ReceivableDeltaMinor && OnAccountDeltaMinor == p.OnAccountDeltaMinor
        && OrderId == p.OrderId && PaymentId == p.PaymentId && AdjustmentId == p.AdjustmentId && CreditNoteId == p.CreditNoteId;
}
