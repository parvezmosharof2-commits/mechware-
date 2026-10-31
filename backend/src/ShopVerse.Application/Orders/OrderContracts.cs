namespace ShopVerse.Application.Orders;

/// <summary>What the buyer asked for. There is deliberately no price, tax, discount or total here: the server decides them.</summary>
public sealed record PlaceOrderLine(Guid VariantId, int Quantity);

public sealed record PlaceOrderRequest(
    Guid UserId,
    string IdempotencyKey,
    IReadOnlyList<PlaceOrderLine> Lines,
    string? ShipToJson = null,
    Guid? CompanyId = null,
    bool PayOnCredit = false)
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 10_000;
}

public sealed record PlacedOrder(
    Guid OrderId, string OrderNumber, string Status, long GrandTotalMinor, DateTime ReservationExpiresAtUtc, bool Replayed);

public sealed record OrderItemDto(Guid VariantId, string Sku, string Title, int Quantity, long UnitPriceMinor, long DiscountMinor, long TaxMinor, long TotalMinor);

public sealed record SubOrderDto(Guid Id, string SubOrderNumber, Guid SellerId, string Status, long ShippingMinor, long TotalMinor, IReadOnlyList<OrderItemDto> Items);

public sealed record OrderDto(
    Guid Id, string OrderNumber, string Status, string Currency, long SubtotalMinor, long DiscountMinor, long TaxMinor,
    long ShippingMinor, long GrandTotalMinor, DateTime PlacedAtUtc, IReadOnlyList<SubOrderDto> SubOrders);

public interface IOrderService
{
    /// <summary>The buyer's own order. Someone else's order answers "not found", never "forbidden", so ids reveal nothing.</summary>
    Task<OrderDto> GetAsync(Guid userId, Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// Re-prices the lines from the database, reserves stock, and writes the order, its sub-orders, its first event and
    /// the idempotency record in one transaction. Retrying the same <c>IdempotencyKey</c> with the same request returns
    /// the original order; the same key with a different request is refused.
    /// </summary>
    Task<PlacedOrder> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default);
}
