namespace Mechware.Application.Orders;

/// <summary>What the buyer asked for. There is deliberately no price, tax, discount or total here: the server decides them.</summary>
public sealed record PlaceOrderLine(Guid VariantId, int Quantity);

/// <summary>
/// Where the order goes is chosen, never typed in: <see cref="ShippingAddressId"/> is one of the buyer's saved addresses,
/// <see cref="ShipToSiteId"/> a delivery site of <see cref="CompanyId"/>. With neither, a company order goes to the company's
/// default delivery site and a personal order to the buyer's default shipping address. The server copies the address
/// into the order, so later edits to the address book never change where a placed order was sent.
/// </summary>
public sealed record PlaceOrderRequest(
    Guid UserId,
    string IdempotencyKey,
    IReadOnlyList<PlaceOrderLine> Lines,
    Guid? ShippingAddressId = null,
    Guid? CompanyId = null,
    bool PayOnCredit = false,
    IReadOnlyList<string>? CouponCodes = null,
    Guid? ShipToSiteId = null,
    Guid? CostCentreId = null,
    string? PurchaseOrderNumber = null)
{
    public const int MaxCoupons = 3;
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 10_000;

    /// <summary>
    /// Every unpaid order holds its stock for 30 minutes. Without a cap one account could keep any product "out of stock"
    /// for everyone else by placing order after order and never paying (finding #2 of the 2026-11-04 test).
    /// </summary>
    public const int MaxOpenUnpaidOrdersPerBuyer = 3;

    /// <summary>The approval subject type of an order: such requests are created only by placing the order (threat model v1, H1).</summary>
    public const string ApprovalSubjectType = "order";
}

public sealed record PlacedOrder(
    Guid OrderId, string OrderNumber, string Status, long GrandTotalMinor, DateTime ReservationExpiresAtUtc, bool Replayed);

public sealed record OrderItemDto(Guid VariantId, string Sku, string Title, int Quantity, long UnitPriceMinor, long DiscountMinor, long TaxMinor, long TotalMinor);

public sealed record SubOrderDto(Guid Id, string SubOrderNumber, Guid SellerId, string Status, long ShippingMinor, long TotalMinor, IReadOnlyList<OrderItemDto> Items);

public sealed record OrderDto(
    Guid Id, string OrderNumber, string Status, string Currency, long SubtotalMinor, long DiscountMinor, long TaxMinor,
    long ShippingMinor, long GrandTotalMinor, DateTime PlacedAtUtc, IReadOnlyList<SubOrderDto> SubOrders, string? PurchaseOrderNumber = null);

/// <summary>What the checkout would charge for delivery, without placing anything (E8.2; the checkout page is E3.4).</summary>
public sealed record ShippingQuoteRequest(
    Guid UserId, IReadOnlyList<PlaceOrderLine> Lines, Guid? ShippingAddressId = null, Guid? CompanyId = null,
    IReadOnlyList<string>? CouponCodes = null, Guid? ShipToSiteId = null);

/// <summary>One seller's delivery fee; <paramref name="StoreName"/> is the storefront name the buyer sees, for the review step.</summary>
public sealed record SellerShippingDto(Guid SellerId, long ShippingMinor, string StoreName);

/// <summary>
/// <paramref name="TaxMinor"/> and <paramref name="GrandTotalMinor"/> are what placing the same lines would total (the same
/// pricing path, VAT as placement treats it, shipping a final amount with no VAT split), so the review step shows a real total.
/// </summary>
public sealed record ShippingQuoteDto(
    IReadOnlyList<SellerShippingDto> PerSeller, long TotalMinor, bool FreeShippingApplied, long TaxMinor, long GrandTotalMinor);

/// <summary>One row of the buyer's order list. No cost, margin or seller-payout fields.</summary>
public sealed record OrderSummaryDto(
    Guid Id, string OrderNumber, string Status, long GrandTotalMinor, DateTime CreatedAt, int ItemCount, Guid? CompanyId, string? PurchaseOrderNumber);

public interface IOrderService
{
    /// <summary>
    /// The buyer cancels their own order while it is still pending (payment or approval): the order is cancelled, the event
    /// recorded with the reason, and the held stock released. Anything further along is refused until stock can be returned.
    /// </summary>
    Task<OrderDto> CancelAsync(Guid userId, Guid orderId, string reason, CancellationToken ct = default);

    /// <summary>Cancels orders still unpaid 30 minutes after placement (brief §8.6) and frees their stock. Returns how many.</summary>
    Task<int> ExpireUnpaidAsync(DateTime nowUtc, int batchSize = 200, CancellationToken ct = default);

    /// <summary>Applies decided (or lapsed) company approvals to the orders waiting for them; run by the expiry sweep.</summary>
    Task<int> SettleApprovalsAsync(DateTime nowUtc, int batchSize = 200, CancellationToken ct = default);

    /// <summary>Applies one order's approval outcome right after the decision. Returns false when there was nothing to do.</summary>
    Task<bool> ApplyApprovalAsync(Guid orderId, CancellationToken ct = default);

    /// <summary>The buyer's own order. Someone else's order answers "not found", never "forbidden", so ids reveal nothing.</summary>
    Task<OrderDto> GetAsync(Guid userId, Guid orderId, CancellationToken ct = default);

    /// <summary>
    /// The orders this user placed, newest first (same visibility rule as <see cref="GetAsync"/>: own orders only, whether or not
    /// they were placed for a company). <paramref name="pageSize"/> is capped at <see cref="MaxListPageSize"/>.
    /// </summary>
    Task<Mechware.Application.Catalog.PagedResult<OrderSummaryDto>> ListAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);

    public const int MaxListPageSize = 50;

    /// <summary>
    /// Re-prices the lines from the database, reserves stock, and writes the order, its sub-orders, its first event and
    /// the idempotency record in one transaction. Retrying the same <c>IdempotencyKey</c> with the same request returns
    /// the original order; the same key with a different request is refused.
    /// </summary>
    Task<PlacedOrder> PlaceAsync(PlaceOrderRequest request, CancellationToken ct = default);

    /// <summary>
    /// The shipping <see cref="PlaceAsync"/> would charge for these lines and destination, computed the same way and from the
    /// database only; holds no stock and writes no order. Refusals are the same 422 codes placement gives.
    /// </summary>
    Task<ShippingQuoteDto> QuoteShippingAsync(ShippingQuoteRequest request, CancellationToken ct = default);
}
