using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Credit;
using Mechware.Domain.Orders;
using Mechware.Domain.Quotes;

namespace Mechware.Domain.Returns;

/// <summary>
/// F2.1 opens <see cref="Requested"/> and the buyer may withdraw it until staff decide. F2.3a: Requested → Approved | Rejected,
/// Approved → Received → Inspected (awaiting the refund or credit note of F2.3b: Inspected → Refunded | CreditNoted).
/// </summary>
public enum ReturnStatus { Requested = 1, Approved = 2, Rejected = 3, Received = 4, Refunded = 5, CreditNoted = 6, Withdrawn = 7, Inspected = 8 }

public enum ReturnReason { Damaged = 1, WrongItem = 2, NotAsDescribed = 3, Other = 4 }

/// <summary>A buyer asked to return lines of a delivered order (F2.1). Published through the outbox for staff and mails (F3).</summary>
public sealed record ReturnRequestedEvent(Guid ReturnId, Guid OrderId, string OrderNumber, Guid RequestedBy, Guid? CompanyId, int Quantity) : DomainEventBase;

/// <summary>The buyer withdrew an open return request; its quantities are free again.</summary>
public sealed record ReturnWithdrawnEvent(Guid ReturnId, Guid OrderId, string OrderNumber, Guid RequestedBy) : DomainEventBase;

public sealed record ReturnApprovedEvent(Guid ReturnId, Guid OrderId, string OrderNumber, Guid RequestedBy) : DomainEventBase;

/// <summary>Staff refused the return; its quantities are free again (as after a withdrawal).</summary>
public sealed record ReturnRejectedEvent(Guid ReturnId, Guid OrderId, string OrderNumber, Guid RequestedBy) : DomainEventBase;

public sealed record ReturnReceivedEvent(Guid ReturnId, Guid OrderId, string OrderNumber, Guid RequestedBy) : DomainEventBase;

/// <summary>Inspected: what was accepted (to refund or credit, F2.3b) and how much of it goes back on the shelf.</summary>
public sealed record ReturnInspectedEvent(Guid ReturnId, Guid OrderId, string OrderNumber, Guid RequestedBy, int AcceptedQuantity, int RestockQuantity) : DomainEventBase;

/// <summary>The inspection of one return line: accepted 0..asked, restocked 0..accepted (the rest is scrapped), an internal note.</summary>
public sealed record ReturnInspection(Guid ReturnLineId, int AcceptedQuantity, int RestockQuantity, string? Note);

/// <summary>One asked line: an order line id and how many of it.</summary>
public sealed record ReturnLineRequest(Guid OrderItemId, int Quantity);

/// <summary>
/// What one line of the order allows: the quantity delivered (the whole line while an order has one shipment, F1.4 changes that),
/// what other open returns already claim, and whether its category may be returned at all.
/// </summary>
public sealed record ReturnableLine(Guid OrderItemId, int DeliveredQuantity, int AlreadyClaimed, bool Returnable);

public sealed class ReturnRequestLine : Entity<Guid>
{
    public Guid ReturnRequestId { get; private set; }
    public Guid OrderItemId { get; private set; }
    public int Quantity { get; private set; }
    /// <summary>Null until inspected (F2.3a); then 0..<see cref="Quantity"/>.</summary>
    public int? AcceptedQuantity { get; private set; }
    /// <summary>Null until inspected; then 0..<see cref="AcceptedQuantity"/> (accepted but not restocked = scrapped).</summary>
    public int? RestockQuantity { get; private set; }
    /// <summary>Internal inspection note; never shown to the buyer.</summary>
    public string? InspectionNote { get; private set; }

    private ReturnRequestLine() { }

    internal void Inspect(int accepted, int restock, string? note)
    {
        AcceptedQuantity = accepted;
        RestockQuantity = restock;
        InspectionNote = note;
    }

    internal ReturnRequestLine(Guid returnRequestId, Guid orderItemId, int quantity, DateTime nowUtc) : base(Guid.NewGuid())
    {
        ReturnRequestId = returnRequestId;
        OrderItemId = orderItemId;
        Quantity = quantity;
        CreatedAtUtc = nowUtc;
    }
}

/// <summary>
/// A return request (RMA) of a delivered order (F2.1). Rules here: the order is delivered, the request comes within the window
/// (<see cref="WindowEndsUtc"/>), every line belongs to the order, may be returned, and asks at most what was delivered minus what
/// other open requests (anything but withdrawn or rejected: those free their units) already claim. The database repeats the quantity rule
/// (<c>ReturnSql.Install</c>), and the service decides it under the order's row lock, so two parallel requests cannot both claim the same units.
/// </summary>
public sealed class ReturnRequest : AggregateRoot<Guid>
{
    public const int NoteMaxLength = 1000;
    public const int MaxLines = 100;
    public const int MaxQuantity = 10_000;

    private readonly List<ReturnRequestLine> _lines = [];

    public Guid OrderId { get; private set; }
    public Guid? CompanyId { get; private set; }
    public Guid RequestedBy { get; private set; }
    public ReturnStatus Status { get; private set; }
    public ReturnReason Reason { get; private set; }
    public string? Note { get; private set; }
    public string IdempotencyKey { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public DateTime RequestedAtUtc { get; private set; }
    public DateTime? WithdrawnAtUtc { get; private set; }
    /// <summary>The staff member who approved or rejected it, and when.</summary>
    public Guid? DecidedBy { get; private set; }
    public DateTime? DecidedAtUtc { get; private set; }
    /// <summary>Why it was rejected (5-500 plain text); the buyer sees it.</summary>
    public string? RejectionReason { get; private set; }
    public Guid? ReceivedBy { get; private set; }
    public DateTime? ReceivedAtUtc { get; private set; }
    public Guid? InspectedBy { get; private set; }
    public DateTime? InspectedAtUtc { get; private set; }
    public IReadOnlyList<ReturnRequestLine> Lines => _lines;

    public const int ReasonMinLength = 5;
    public const int ReasonMaxLength = 500;
    public const int InspectionNoteMaxLength = 500;

    private ReturnRequest() { }

    /// <summary>
    /// The first instant a return is too late: midnight in Dhaka (+06:00, no daylight saving) after the <paramref name="days"/>-th
    /// calendar day following the Dhaka delivery day. Delivered on the 1st at any hour, 7 days: open until the 8th, 23:59:59 Dhaka.
    /// </summary>
    public static DateTime WindowEndsUtc(DateTime deliveredAtUtc, int days)
    {
        var lastDay = CreditCalendar.DhakaDate(deliveredAtUtc).AddDays(days + 1);
        return DateTime.SpecifyKind(lastDay.ToDateTime(TimeOnly.MinValue).AddHours(-6), DateTimeKind.Utc);
    }

    public static ReturnReason ParseReason(string? code) => code?.Trim().ToLowerInvariant() switch
    {
        "damaged" => ReturnReason.Damaged,
        "wrong_item" => ReturnReason.WrongItem,
        "not_as_described" => ReturnReason.NotAsDescribed,
        "other" => ReturnReason.Other,
        _ => throw new ValidationException("reasonCode", "Choose a reason: damaged, wrong_item, not_as_described or other.")
    };

    /// <summary>Plain text, trimmed, blank = none, at most 1000 characters. A reason of <c>other</c> needs a note.</summary>
    public static string? NormaliseNote(string? value, ReturnReason reason)
    {
        var note = PlainText.Clean(value)?.Trim();
        if (string.IsNullOrEmpty(note)) note = null;
        if (note is { Length: > NoteMaxLength }) throw new ValidationException("note", $"A note has at most {NoteMaxLength} characters.");
        if (note is null && reason == ReturnReason.Other) throw new ValidationException("note", "Tell us what is wrong when the reason is 'other'.");
        return note;
    }

    /// <summary>1 to 100 lines, each order line once, each quantity 1 to 10,000.</summary>
    public static IReadOnlyList<ReturnLineRequest> NormaliseLines(IReadOnlyList<ReturnLineRequest>? lines)
    {
        if (lines is not { Count: > 0 and <= MaxLines }) throw new ValidationException("lines", $"Choose 1 to {MaxLines} lines to return.");
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].OrderItemId == Guid.Empty) throw new ValidationException($"lines[{i}].orderItemId", "Name the order line to return.");
            if (lines[i].Quantity is < 1 or > MaxQuantity) throw new ValidationException($"lines[{i}].quantity", $"Return 1 to {MaxQuantity} of a line.");
        }
        if (lines.Select(l => l.OrderItemId).Distinct().Count() != lines.Count) throw new ValidationException("lines", "Name each order line once.");
        return [.. lines.OrderBy(l => l.OrderItemId)];
    }

    /// <param name="deliveredAtUtc">When the order was delivered; null for an order that never was.</param>
    public static ReturnRequest Open(
        Guid orderId, string orderNumber, OrderStatus orderStatus, DateTime? deliveredAtUtc, Guid? companyId, Guid requestedBy,
        ReturnReason reason, string? note, IReadOnlyList<ReturnLineRequest> lines, IReadOnlyList<ReturnableLine> orderLines, int windowDays,
        DateTime nowUtc, string idempotencyKey, string requestHash)
    {
        if (orderStatus != OrderStatus.Delivered || deliveredAtUtc is not { } delivered)
            throw new ConflictException("RETURN_ORDER_NOT_DELIVERED", "A return can be opened only for a delivered order.");
        if (nowUtc >= WindowEndsUtc(delivered, windowDays))
            throw new ConflictException("RETURN_WINDOW_CLOSED", $"Returns are open for {windowDays} days after delivery; this order's window has closed.");

        var byId = orderLines.ToDictionary(l => l.OrderItemId);
        foreach (var line in lines)
        {
            if (!byId.TryGetValue(line.OrderItemId, out var allowed))
                throw new ValidationException("lines", "A line to return is not on this order.");
            if (!allowed.Returnable)
                throw new UnprocessableException("RETURN_LINE_NOT_RETURNABLE", "One of these items cannot be returned (its category is not returnable).");
            if (line.Quantity > allowed.DeliveredQuantity - allowed.AlreadyClaimed)
                throw new ConflictException("RETURN_QUANTITY_EXCEEDED",
                    $"At most {Math.Max(0, allowed.DeliveredQuantity - allowed.AlreadyClaimed)} more of a line can be returned.");
        }

        var request = new ReturnRequest
        {
            Id = Guid.NewGuid(),
            OrderId = orderId,
            CompanyId = companyId,
            RequestedBy = requestedBy,
            Status = ReturnStatus.Requested,
            Reason = reason,
            Note = note,
            IdempotencyKey = idempotencyKey,
            RequestHash = requestHash,
            RequestedAtUtc = nowUtc,
            CreatedAtUtc = nowUtc
        };
        foreach (var line in lines) request._lines.Add(new ReturnRequestLine(request.Id, line.OrderItemId, line.Quantity, nowUtc));
        request.AddDomainEvent(new ReturnRequestedEvent(request.Id, orderId, orderNumber, requestedBy, companyId, lines.Sum(l => l.Quantity)));
        return request;
    }

    /// <summary>Requested → withdrawn by the buyer. False when it already was (nothing changes); any later status is 409.</summary>
    public bool Withdraw(string orderNumber, DateTime nowUtc)
    {
        if (Status == ReturnStatus.Withdrawn) return false;
        if (Status != ReturnStatus.Requested)
            throw new ConflictException("RETURN_NOT_WITHDRAWABLE", "This return is already being handled and can no longer be withdrawn.");
        Status = ReturnStatus.Withdrawn;
        WithdrawnAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
        AddDomainEvent(new ReturnWithdrawnEvent(Id, OrderId, orderNumber, RequestedBy));
        return true;
    }

    /// <summary>Plain text, trimmed, 5 to 500 characters.</summary>
    public static string NormaliseRejectionReason(string? value)
    {
        var reason = PlainText.Clean(value)?.Trim();
        if (reason is not { Length: >= ReasonMinLength and <= ReasonMaxLength })
            throw new ValidationException("reason", $"Give a reason of {ReasonMinLength} to {ReasonMaxLength} characters.");
        return reason;
    }

    /// <summary>
    /// The shape of an inspection, before anything is looked up: 1 to 100 lines, each return line once, accepted ≥ 0, restocked 0..accepted,
    /// note plain text ≤ 500 (blank = none). Whether accepted ≤ asked is checked against the return in <see cref="Inspect"/>.
    /// </summary>
    public static IReadOnlyList<ReturnInspection> NormaliseInspection(IReadOnlyList<ReturnInspection>? lines)
    {
        if (lines is not { Count: > 0 and <= MaxLines }) throw new ValidationException("lines", "Inspect every line of this return once.");
        var result = new List<ReturnInspection>(lines.Count);
        for (var i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (l.ReturnLineId == Guid.Empty) throw new ValidationException($"lines[{i}].returnLineId", "Name the return line.");
            if (l.AcceptedQuantity is < 0 or > MaxQuantity) throw new ValidationException($"lines[{i}].acceptedQty", "Accept 0 up to the quantity asked.");
            if (l.RestockQuantity < 0 || l.RestockQuantity > l.AcceptedQuantity)
                throw new ValidationException($"lines[{i}].restockQty", "Restock 0 up to the accepted quantity.");
            var note = PlainText.Clean(l.Note)?.Trim();
            if (string.IsNullOrEmpty(note)) note = null;
            if (note is { Length: > InspectionNoteMaxLength })
                throw new ValidationException($"lines[{i}].note", $"A note has at most {InspectionNoteMaxLength} characters.");
            result.Add(l with { Note = note });
        }
        if (result.Select(l => l.ReturnLineId).Distinct().Count() != result.Count) throw new ValidationException("lines", "Name each return line once.");
        return result;
    }

    /// <summary>Requested → approved: the buyer may no longer withdraw it.</summary>
    public void Approve(Guid actor, string orderNumber, DateTime nowUtc)
    {
        Ensure(ReturnStatus.Requested, "approved");
        Status = ReturnStatus.Approved;
        (DecidedBy, DecidedAtUtc, UpdatedAtUtc) = (actor, nowUtc, nowUtc);
        AddDomainEvent(new ReturnApprovedEvent(Id, OrderId, orderNumber, RequestedBy));
    }

    /// <summary>Requested → rejected with a reason (<see cref="NormaliseRejectionReason"/>); its quantities are free again.</summary>
    public void Reject(Guid actor, string reason, string orderNumber, DateTime nowUtc)
    {
        Ensure(ReturnStatus.Requested, "rejected");
        Status = ReturnStatus.Rejected;
        (DecidedBy, DecidedAtUtc, RejectionReason, UpdatedAtUtc) = (actor, nowUtc, reason, nowUtc);
        AddDomainEvent(new ReturnRejectedEvent(Id, OrderId, orderNumber, RequestedBy));
    }

    /// <summary>Approved → received: the goods arrived.</summary>
    public void MarkReceived(Guid actor, string orderNumber, DateTime nowUtc)
    {
        Ensure(ReturnStatus.Approved, "marked received");
        Status = ReturnStatus.Received;
        (ReceivedBy, ReceivedAtUtc, UpdatedAtUtc) = (actor, nowUtc, nowUtc);
        AddDomainEvent(new ReturnReceivedEvent(Id, OrderId, orderNumber, RequestedBy));
    }

    /// <summary>
    /// Received → inspected. <paramref name="lines"/> (already normalised) name every line of this return exactly once; each accepts at most
    /// what it asked and restocks at most what it accepted. The restock itself happens after the commit (the service).
    /// </summary>
    public void Inspect(Guid actor, IReadOnlyList<ReturnInspection> lines, string orderNumber, DateTime nowUtc)
    {
        Ensure(ReturnStatus.Received, "inspected");
        var byId = _lines.ToDictionary(l => l.Id);
        if (lines.Count != _lines.Count || lines.Any(l => !byId.ContainsKey(l.ReturnLineId)))
            throw new ValidationException("lines", "Inspect every line of this return once.");
        for (var i = 0; i < lines.Count; i++)
            if (lines[i].AcceptedQuantity > byId[lines[i].ReturnLineId].Quantity)
                throw new ValidationException($"lines[{i}].acceptedQty", "Accept 0 up to the quantity asked.");
        foreach (var l in lines) byId[l.ReturnLineId].Inspect(l.AcceptedQuantity, l.RestockQuantity, l.Note);
        Status = ReturnStatus.Inspected;
        (InspectedBy, InspectedAtUtc, UpdatedAtUtc) = (actor, nowUtc, nowUtc);
        AddDomainEvent(new ReturnInspectedEvent(Id, OrderId, orderNumber, RequestedBy, lines.Sum(l => l.AcceptedQuantity), lines.Sum(l => l.RestockQuantity)));
    }

    private void Ensure(ReturnStatus from, string action)
    {
        if (Status != from)
            throw new ConflictException("RETURN_INVALID_TRANSITION", $"A return that is {Status.ToString().ToLowerInvariant()} cannot be {action}.");
    }
}
