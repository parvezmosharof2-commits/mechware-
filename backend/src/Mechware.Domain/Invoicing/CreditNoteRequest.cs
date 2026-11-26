using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Quotes;

namespace Mechware.Domain.Invoicing;

/// <summary>Request → Approved (a 6.7 is issued) | Rejected | Withdrawn (ADR-0012 §9.3); nothing moves after that.</summary>
public enum CreditNoteRequestStatus
{
    Requested,
    Approved,
    Rejected,
    Withdrawn
}

/// <summary>One planned line as asked: the original line, the credited quantity, value excl. VAT and VAT shown to the approver.</summary>
public sealed record CreditNoteRequestLine(Guid OriginalLineId, int Quantity, long NetMinor, long VatMinor);

/// <summary>
/// A credit-note request (ADR-0012 §9.3, §16): it never has a number; only its approval by another person (E6.5b) issues the 6.7, in that
/// transaction, after planning again under the original invoice's lock. The reason text is already cleaned by <see cref="CreditNotePlanner"/>.
/// <c>CreditNoteSql</c> keeps the asked content fixed and allows one move out of Requested.
/// </summary>
public sealed class CreditNoteRequest : Entity<Guid>
{
    public Guid OriginalInvoiceId { get; private set; }
    public Guid OrderId { get; private set; }
    public CreditNoteRequestStatus Status { get; private set; }
    public CreditNoteReason Reason { get; private set; }
    public string ReasonText { get; private set; } = string.Empty;
    public IReadOnlyList<CreditNoteRequestLine> Lines { get; private set; } = [];
    public long TotalMinor { get; private set; }
    public Guid RequestedBy { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public string? SourceKey { get; private set; }
    public Guid? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    public string? DecisionReason { get; private set; }
    public Guid? IssuedInvoiceId { get; private set; }

    private CreditNoteRequest() { }

    /// <summary>A new request from a plan of <paramref name="original"/>; <paramref name="sourceKey"/> is <c>return:{id:N}</c> for an F2 return.</summary>
    public static CreditNoteRequest Create(Guid id, Invoice original, CreditNotePlan plan, Guid requestedBy, string idempotencyKey, string requestHash,
        string? sourceKey, DateTime nowUtc)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.OriginalInvoiceId != original.Id) throw new InvalidOperationException("The plan belongs to another invoice.");
        return new CreditNoteRequest
        {
            Id = id,
            OriginalInvoiceId = original.Id,
            OrderId = original.OrderId,
            Status = CreditNoteRequestStatus.Requested,
            Reason = plan.Reason,
            ReasonText = plan.ReasonText,
            Lines = [.. plan.Lines.Select(l => new CreditNoteRequestLine(l.OriginalLineId, l.Quantity, l.NetMinor, l.VatMinor))],
            TotalMinor = plan.TotalMinor,
            RequestedBy = requestedBy,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            SourceKey = sourceKey,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc
        };
    }

    public const string FourEyes = "CREDIT_NOTE_FOUR_EYES";
    public const string NotRequested = "CREDIT_NOTE_NOT_REQUESTED";
    public const string NotYours = "CREDIT_NOTE_NOT_YOURS";

    /// <summary>Four eyes (IT4): only an open request, and never by the person who asked (they withdraw instead). Checked before any lock-heavy work.</summary>
    public void EnsureDecidableBy(Guid actor)
    {
        if (Status != CreditNoteRequestStatus.Requested) throw new ConflictException(NotRequested, "This credit-note request is already decided or withdrawn.");
        if (actor == RequestedBy) throw new ConflictException(FourEyes, "Another person approves or rejects a credit note; withdraw your own request instead.");
    }

    /// <summary>The approval: names the 6.7 issued in the same transaction (the DB guard checks it is that request's credit note).</summary>
    public void Approve(Guid actor, Guid creditNoteId, DateTime nowUtc)
    {
        EnsureDecidableBy(actor);
        (Status, DecidedBy, DecidedAtUtc, IssuedInvoiceId, UpdatedAtUtc) = (CreditNoteRequestStatus.Approved, actor, nowUtc, creditNoteId, nowUtc);
    }

    /// <summary>A refusal by another person, with a plain-text reason of 5–500 characters; nothing is issued and nothing moves.</summary>
    public void Reject(Guid actor, string? reason, DateTime nowUtc)
    {
        EnsureDecidableBy(actor);
        var text = PlainText.Clean(reason)?.Trim();
        if (text is not { Length: >= 5 and <= 500 }) throw new ValidationException("reason", "Give a reason of 5 to 500 characters.");
        (Status, DecidedBy, DecidedAtUtc, DecisionReason, UpdatedAtUtc) = (CreditNoteRequestStatus.Rejected, actor, nowUtc, text, nowUtc);
    }

    /// <summary>The requester takes their own open request back.</summary>
    public void Withdraw(Guid actor, DateTime nowUtc)
    {
        if (Status != CreditNoteRequestStatus.Requested) throw new ConflictException(NotRequested, "This credit-note request is already decided or withdrawn.");
        if (actor != RequestedBy) throw new ConflictException(NotYours, "Only the person who asked may withdraw a request; reject it instead.");
        (Status, DecidedBy, DecidedAtUtc, UpdatedAtUtc) = (CreditNoteRequestStatus.Withdrawn, actor, nowUtc, nowUtc);
    }
}
