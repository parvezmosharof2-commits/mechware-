namespace ShopVerse.Application.Orders;

/// <summary>What the buyer asked for. There is deliberately no price, tax, discount or total here: the server decides them.</summary>
public sealed record PlaceOrderLine(Guid VariantId, int Quantity);

public sealed record PlaceOrderRequest(
    Guid UserId,
    string IdempotencyKey,
    IReadOnlyList<PlaceOrderLine> Lines,
    string? ShipToJson = null,
    Guid? CompanyId = null)
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 10_000;
}

public sealed record PlacedOrder(
    Guid OrderId, string OrderNumber, string Status, long GrandTotalMinor, DateTime ReservationExpiresAtUtc, bool Replayed);

public interface IOrderService
{
    /// <summary>
    /// Re-prices the lines from the database, reserves stock, and writes the order, its sub-orders, its first event and
    /// the idempotency record in one transaction. Retrying the same <c>IdempotencyKey</c> with the same request returns
    /// the original order; the same key with a different request is refused.
    /// </summary>
    Task<PlacedOrder> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default);
}
