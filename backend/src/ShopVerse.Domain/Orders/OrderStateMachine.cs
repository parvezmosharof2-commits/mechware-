using System.Security.Cryptography;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Domain.Orders;

/// <summary>Order lifecycle, brief §8.6. The wire/database names are the snake_case form of these.</summary>
public enum OrderStatus
{
    PendingApproval = 1,
    PendingPayment = 2,
    Confirmed = 3,
    Processing = 4,
    Packed = 5,
    Shipped = 6,
    OutForDelivery = 7,
    Delivered = 8,
    Completed = 9,
    Cancelled = 10,
    DeliveryFailed = 11,
    Returned = 12,
    ReturnRequested = 13,
    ReturnApproved = 14,
    Refunded = 15
}

/// <summary>One recorded change of an order's state. Every change creates one; nothing else may change the state.</summary>
public sealed record OrderEvent(Guid OrderId, OrderStatus From, OrderStatus To, Guid? ActorId, string? Reason, DateTime AtUtc);

/// <summary>
/// The single place that says which order state may follow which (brief §8.6). Anything not listed is refused, so a
/// shipped order cannot be cancelled and a completed one cannot reopen. A reason is mandatory where the business needs
/// to explain itself later: cancelling, a failed delivery, and a return decision.
/// </summary>
public static class OrderStateMachine
{
    private static readonly IReadOnlyDictionary<OrderStatus, OrderStatus[]> Allowed = new Dictionary<OrderStatus, OrderStatus[]>
    {
        [OrderStatus.PendingApproval] = [OrderStatus.PendingPayment, OrderStatus.Cancelled],
        [OrderStatus.PendingPayment] = [OrderStatus.Confirmed, OrderStatus.Cancelled],
        [OrderStatus.Confirmed] = [OrderStatus.Processing, OrderStatus.Cancelled],
        [OrderStatus.Processing] = [OrderStatus.Packed, OrderStatus.Cancelled],
        [OrderStatus.Packed] = [OrderStatus.Shipped, OrderStatus.Cancelled],
        [OrderStatus.Shipped] = [OrderStatus.OutForDelivery, OrderStatus.DeliveryFailed],
        [OrderStatus.OutForDelivery] = [OrderStatus.Delivered, OrderStatus.DeliveryFailed],
        [OrderStatus.Delivered] = [OrderStatus.Completed, OrderStatus.ReturnRequested],
        [OrderStatus.DeliveryFailed] = [OrderStatus.OutForDelivery, OrderStatus.Returned],
        [OrderStatus.Returned] = [OrderStatus.Refunded],
        [OrderStatus.ReturnRequested] = [OrderStatus.ReturnApproved, OrderStatus.Delivered],
        [OrderStatus.ReturnApproved] = [OrderStatus.Refunded],
        [OrderStatus.Completed] = [],
        [OrderStatus.Cancelled] = [],
        [OrderStatus.Refunded] = []
    };

    private static readonly HashSet<OrderStatus> NeedsReason =
        [OrderStatus.Cancelled, OrderStatus.DeliveryFailed, OrderStatus.ReturnApproved];

    public static IReadOnlyList<OrderStatus> NextStates(OrderStatus from) => Allowed[from];

    public static bool CanTransition(OrderStatus from, OrderStatus to) => Allowed[from].Contains(to);

    public static bool IsTerminal(OrderStatus status) => Allowed[status].Length == 0;

    /// <summary>Whether stock still held for the order can be released (nothing has left the warehouse yet).</summary>
    public static bool HoldsStock(OrderStatus status) =>
        status is OrderStatus.PendingPayment or OrderStatus.Confirmed or OrderStatus.Processing or OrderStatus.Packed;

    public static OrderEvent Transition(Guid orderId, OrderStatus from, OrderStatus to, Guid? actorId, string? reason, DateTime nowUtc)
    {
        if (!CanTransition(from, to))
            throw new ConflictException("ORDER_INVALID_TRANSITION", $"An order cannot move from {from} to {to}.");
        if (NeedsReason.Contains(to) && string.IsNullOrWhiteSpace(reason))
            throw new ValidationException("reason", $"A reason is required to move an order to {to}.");

        return new OrderEvent(orderId, from, to, actorId, reason?.Trim(), nowUtc);
    }
}

/// <summary>Order numbers: <c>MW-YYMMDD-XXXXXX</c> with six random base32 characters (brief §8.6).</summary>
public static class OrderNumber
{
    // RFC 4648 base32 without padding. Ambiguous look-alikes are kept: the number is read from screens, not handwriting.
    private const string Alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZ234567";

    public static string Generate(DateTime nowUtc, Func<int, int>? randomIndex = null)
    {
        randomIndex ??= RandomNumberGenerator.GetInt32;
        var suffix = string.Create(6, randomIndex, static (span, rnd) =>
        {
            for (var i = 0; i < span.Length; i++) span[i] = Alphabet[rnd(Alphabet.Length)];
        });
        return $"MW-{nowUtc:yyMMdd}-{suffix}";
    }

    public static bool IsValid(string? value) =>
        value is { Length: 16 }
        && value.StartsWith("MW-", StringComparison.Ordinal)
        && value.AsSpan(3, 6).IndexOfAnyExceptInRange('0', '9') < 0
        && value[9] == '-'
        && value.AsSpan(10).IndexOfAnyExcept(Alphabet) < 0;
}
