using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Credit;
using Mechware.Domain.Orders;
using Mechware.Domain.Quotes;

namespace Mechware.Domain.Returns;

/// <summary>F2.1 opens <see cref="Requested"/> and the buyer may withdraw it; F2.3 decides, receives and settles (refund or credit note).</summary>
public enum ReturnStatus { Requested = 1, Approved = 2, Rejected = 3, Received = 4, Refunded = 5, CreditNoted = 6, Withdrawn = 7 }

public enum ReturnReason { Damaged = 1, WrongItem = 2, NotAsDescribed = 3, Other = 4 }

/// <summary>A buyer asked to return lines of a delivered order (F2.1). Published through the outbox for staff and mails (F3).</summary>
public sealed record ReturnRequestedEvent(Guid ReturnId, Guid OrderId, string OrderNumber, Guid RequestedBy, Guid? CompanyId, int Quantity) : DomainEventBase;

/// <summary>The buyer withdrew an open return request; its quantities are free again.</summary>
public sealed record ReturnWithdrawnEvent(Guid ReturnId, Guid OrderId, string OrderNumber, Guid RequestedBy) : DomainEventBase;

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

    private ReturnRequestLine() { }

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
    public IReadOnlyList<ReturnRequestLine> Lines => _lines;

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
}
