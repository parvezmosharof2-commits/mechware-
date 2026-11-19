using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Quotes;

/// <summary>A requested line as the buyer asked it, after D5.1 parsing and D5.2 matching. There is no price field (QT2).</summary>
public sealed record RfqLineInput(string RawText, string? PartKey, int Quantity, RfqMatchStatus MatchStatus, Guid? VariantId,
    Guid? SuccessorVariantId, string? Note);

/// <summary>
/// A request for quote of one Active company (ADR-0011 §2). Its company, requester, source, number and lines never change;
/// staff move it Submitted → Quoting → Answered → Closed, and the buyer may withdraw it while it is open. Who may submit, the
/// caps and the idempotency look-up are the caller's (D4.5); free text arrives already cleaned of control characters (QT17).
/// </summary>
public sealed class Rfq : AggregateRoot<Guid>
{
    private static readonly HashSet<(RfqStatus, RfqStatus)> Allowed =
    [
        (RfqStatus.Submitted, RfqStatus.Quoting), (RfqStatus.Quoting, RfqStatus.Answered),
        (RfqStatus.Submitted, RfqStatus.Closed), (RfqStatus.Quoting, RfqStatus.Closed), (RfqStatus.Answered, RfqStatus.Closed),
        (RfqStatus.Submitted, RfqStatus.Withdrawn), (RfqStatus.Quoting, RfqStatus.Withdrawn), (RfqStatus.Answered, RfqStatus.Withdrawn),
    ];

    private readonly List<RfqLine> _lines = [];

    public string Number { get; private set; } = string.Empty;
    public Guid CompanyId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public RfqSource Source { get; private set; }
    public RfqStatus Status { get; private set; } = RfqStatus.Submitted;
    public string? Message { get; private set; }
    public DateOnly? NeededBy { get; private set; }
    public Guid? ShipToSiteId { get; private set; }
    public int LineCount { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public Guid? AssignedTo { get; private set; }
    public DateTime? FirstQuoteSentAtUtc { get; private set; }
    public Guid? ClosedBy { get; private set; }
    public DateTime? ClosedAtUtc { get; private set; }
    public string? CloseReason { get; private set; }
    public DateTime? WithdrawnAtUtc { get; private set; }
    public IReadOnlyCollection<RfqLine> Lines => _lines.AsReadOnly();

    private Rfq() { }

    public static bool CanMove(RfqStatus from, RfqStatus to) => Allowed.Contains((from, to));

    public static Rfq Submit(Guid id, string number, Guid companyId, Guid requestedBy, RfqSource source, string? message, DateOnly? neededBy,
        Guid? shipToSiteId, IReadOnlyList<RfqLineInput> lines, string idempotencyKey, string requestHash, DateTime nowUtc)
    {
        if (id == Guid.Empty || companyId == Guid.Empty || requestedBy == Guid.Empty)
            throw new ValidationException("id", "An RFQ needs an id, a company and a requester.");
        if (!QuoteNumber.IsRfqNumber(number)) throw new ValidationException("number", "Not an RFQ number.");
        if (!Enum.IsDefined(source)) throw new ValidationException("source", "Unknown RFQ source.");
        if (lines is not { Count: > 0 and <= QuoteLimits.MaxRfqLines })
            throw new ValidationException("lines", $"An RFQ has 1 to {QuoteLimits.MaxRfqLines} lines.");
        if (source == RfqSource.Product && (lines.Count != 1 || lines[0].MatchStatus != RfqMatchStatus.Matched))
            throw new ValidationException("lines", "A product RFQ asks for exactly one catalogue item.");
        var today = QuoteValidity.DhakaDate(nowUtc);
        if (neededBy is { } d && (d < today || d > today.AddDays(QuoteLimits.MaxNeededByDaysAhead)))
            throw new ValidationException("neededBy", $"Give a date from today to {QuoteLimits.MaxNeededByDaysAhead} days ahead.");
        if (idempotencyKey is not { Length: >= 8 and <= 100 }) throw new ValidationException("idempotencyKey", "The Idempotency-Key has 8 to 100 characters.");
        if (!QuoteVersion.IsHash(requestHash)) throw new ValidationException("requestHash", "The request hash is a lower-case SHA-256.");

        var rfq = new Rfq
        {
            Id = id,
            Number = number,
            CompanyId = companyId,
            RequestedBy = requestedBy,
            Source = source,
            NeededBy = neededBy,
            Message = Text.Optional(message, QuoteLimits.MaxMessageLength, "message"),
            ShipToSiteId = shipToSiteId,
            LineCount = lines.Count,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            CreatedAtUtc = nowUtc,
            UpdatedAtUtc = nowUtc,
        };
        for (var i = 0; i < lines.Count; i++) rfq._lines.Add(RfqLine.From(Guid.NewGuid(), id, i + 1, lines[i]));
        rfq.AddDomainEvent(new RfqSubmittedEvent(id, companyId) { OccurredOnUtc = nowUtc });
        return rfq;
    }

    public void Assign(Guid? staffId, DateTime nowUtc)
    {
        if (Status is RfqStatus.Closed or RfqStatus.Withdrawn) throw Refused("assigned");
        (AssignedTo, UpdatedAtUtc) = (staffId, nowUtc);
    }

    /// <summary>A quote was created from this RFQ: Submitted → Quoting; a further quote changes nothing.</summary>
    public void StartQuoting(DateTime nowUtc)
    {
        if (Status is RfqStatus.Quoting or RfqStatus.Answered) return;
        Move(RfqStatus.Quoting, "quoted", nowUtc);
    }

    /// <summary>A quote from it was sent: the first one moves Quoting → Answered and stamps the SLA; a withdrawn or closed RFQ stays so.</summary>
    public void RecordQuoteSent(DateTime nowUtc)
    {
        if (Status != RfqStatus.Quoting) return;
        Move(RfqStatus.Answered, "answered", nowUtc);
        FirstQuoteSentAtUtc = nowUtc;
    }

    /// <summary>Staff end it with a reason the buyer sees (for example "no bid").</summary>
    public void Close(Guid closedBy, string reason, DateTime nowUtc)
    {
        if (!CanMove(Status, RfqStatus.Closed)) throw Refused("closed");
        var why = Text.Reason(reason);
        Move(RfqStatus.Closed, "closed", nowUtc);
        (ClosedBy, ClosedAtUtc, CloseReason) = (closedBy, nowUtc, why);
    }

    /// <summary>The buyer withdraws it while open; quotes already sent stay acceptable.</summary>
    public void Withdraw(DateTime nowUtc)
    {
        Move(RfqStatus.Withdrawn, "withdrawn", nowUtc);
        WithdrawnAtUtc = nowUtc;
    }

    private void Move(RfqStatus to, string verb, DateTime nowUtc)
    {
        if (!CanMove(Status, to)) throw Refused(verb);
        (Status, UpdatedAtUtc) = (to, nowUtc);
    }

    private ConflictException Refused(string verb) => new("RFQ_INVALID_TRANSITION", $"A {Status} request cannot be {verb}.");
}

/// <summary>One requested line (§2). Append-only: never changed after insert.</summary>
public sealed class RfqLine : Entity<Guid>
{
    public Guid RfqId { get; private set; }
    public int LineNo { get; private set; }
    public string RawText { get; private set; } = string.Empty;
    public string? PartKey { get; private set; }
    public int Quantity { get; private set; }
    public RfqMatchStatus MatchStatus { get; private set; }
    public Guid? VariantId { get; private set; }
    public Guid? SuccessorVariantId { get; private set; }
    public string? Note { get; private set; }

    private RfqLine() { }

    internal static RfqLine From(Guid id, Guid rfqId, int lineNo, RfqLineInput l)
    {
        ArgumentNullException.ThrowIfNull(l);
        var field = $"lines[{lineNo}]";
        if (!Enum.IsDefined(l.MatchStatus)) throw new ValidationException(field, "Unknown match status.");
        if (l.Quantity is < 1 or > QuoteLimits.MaxQuantityPerLine)
            throw new ValidationException(field, $"Quantity must be 1 to {QuoteLimits.MaxQuantityPerLine}.");
        var needsVariant = l.MatchStatus is RfqMatchStatus.Matched or RfqMatchStatus.Obsolete;
        var hasVariant = l.VariantId is { } v && v != Guid.Empty;
        if (needsVariant != hasVariant || (l.VariantId is not null && !hasVariant))
            throw new ValidationException(field, "A variant is recorded exactly when the line matched or is obsolete.");
        if (l.SuccessorVariantId is { } s && (l.MatchStatus != RfqMatchStatus.Obsolete || s == Guid.Empty))
            throw new ValidationException(field, "Only an obsolete line names a successor.");
        return new RfqLine
        {
            Id = id,
            RfqId = rfqId,
            LineNo = lineNo,
            Quantity = l.Quantity,
            MatchStatus = l.MatchStatus,
            VariantId = l.VariantId,
            SuccessorVariantId = l.SuccessorVariantId,
            RawText = Text.Optional(l.RawText, QuoteLimits.MaxRawTextLength, field) ?? throw new ValidationException(field, "The line text is required."),
            PartKey = Text.Optional(l.PartKey, QuoteLimits.MaxPartKeyLength, field),
            Note = Text.Optional(l.Note, QuoteLimits.MaxRfqNoteLength, field),
        };
    }
}

/// <summary>Trimming and length rules shared by RFQs and quotes.</summary>
internal static class Text
{
    public static string? Optional(string? value, int max, string field)
    {
        var t = PlainText.Clean(value)?.Trim();   // QT17: control characters go before the text is measured
        if (string.IsNullOrEmpty(t)) return null;
        return t.Length <= max ? t : throw new ValidationException(field, $"At most {max} characters.");
    }

    public static string Required(string? value, int max, string field) =>
        Optional(value, max, field) ?? throw new ValidationException(field, $"Give 1 to {max} characters.");

    public static string Reason(string? value)
    {
        var t = (PlainText.Clean(value) ?? string.Empty).Trim();
        return t.Length is >= QuoteLimits.MinReasonLength and <= QuoteLimits.MaxReasonLength ? t
            : throw new ValidationException("reason", $"Give a reason of {QuoteLimits.MinReasonLength} to {QuoteLimits.MaxReasonLength} characters.");
    }
}
