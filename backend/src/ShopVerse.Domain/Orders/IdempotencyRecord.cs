using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Orders;

/// <summary>
/// Remembers that a client's <c>Idempotency-Key</c> already produced an order, with a hash of what was asked. The same key
/// with the same request replays the original answer; the same key with a different request is a client bug and is refused.
/// </summary>
public sealed class IdempotencyRecord : Entity<Guid>
{
    public Guid UserId { get; private set; }
    public string Key { get; private set; } = string.Empty;
    public string RequestHash { get; private set; } = string.Empty;
    public Guid OrderId { get; private set; }

    private IdempotencyRecord() { }

    public IdempotencyRecord(Guid userId, string key, string requestHash, Guid orderId) : base(Guid.NewGuid())
    {
        UserId = userId;
        Key = key;
        RequestHash = requestHash;
        OrderId = orderId;
    }
}
