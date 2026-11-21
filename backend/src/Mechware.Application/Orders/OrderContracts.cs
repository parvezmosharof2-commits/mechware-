namespace Mechware.Application.Orders;

/// <summary>What the buyer asked for. There is deliberately no price, tax, discount or total here: the server decides them.</summary>
public sealed record PlaceOrderLine(Guid VariantId, int Quantity);

/// <summary>
/// Where the order goes is chosen, never typed in: <see cref="ShippingAddressId"/> is one of the buyer's saved addresses,
/// <see cref="ShipToSiteId"/> a delivery site of <see cref="CompanyId"/>. With neither, a company order goes to the company's
/// default delivery site and a personal order to the buyer's default shipping address. The server copies the address
/// into the order, so later edits to the address book never change where a placed order was sent.
/// <para>
/// <see cref="ExpectedGrandTotalMinor"/> is the total the buyer was shown (ADR-0008 §9). It can only stop an order: when the
/// server's own total is higher, nothing is placed (409 <c>PRICE_CHANGED</c>); a lower or equal server total goes through at
/// the server's price. It is not part of the idempotency hash: a replay returns the order already placed.
/// </para>
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
    string? PurchaseOrderNumber = null,
    long? ExpectedGrandTotalMinor = null)
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

/// <summary>
/// Accepting a sent quote version as an order (ADR-0011 §8.1). There are no lines, quantities, prices, coupons or company choice: the
/// order is the quote's version, in the quote's company (<paramref name="CompanyId"/> is the URL's and must be the quote's).
/// <paramref name="ContentHash"/> is the hash of the version the buyer saw; <paramref name="ExpectedGrandTotalMinor"/> (required) is the
/// accept preview's total: a higher server total is 409 <c>PRICE_CHANGED</c>, a lower or equal one goes through at the server's.
/// </summary>
public sealed record AcceptQuoteRequest(
    Guid UserId, string IdempotencyKey, Guid CompanyId, Guid QuoteId, int? VersionNo, string? ContentHash, long? ExpectedGrandTotalMinor,
    bool PayOnCredit = false, Guid? ShipToSiteId = null, Guid? ShippingAddressId = null, Guid? CostCentreId = null, string? PurchaseOrderNumber = null);

/// <param name="QuoteNumber">The accepted quote's number, for an order placed from a quote (ADR-0011 §12); null otherwise.</param>
public sealed record PlacedOrder(
    Guid OrderId, string OrderNumber, string Status, long GrandTotalMinor, DateTime ReservationExpiresAtUtc, bool Replayed, string? QuoteNumber = null);

/// <param name="PriceSource">
/// Where the unit price came from (ADR-0008 §8): <c>list</c>, <c>sale</c>, <c>quantity_break</c>, <c>group_list</c>,
/// <c>contract</c> or <c>quote</c>; null for lines placed before price sources were recorded. No cost, floor or margin.
/// </param>
/// <param name="ContractRef">The buyer's contract number when the price is a contract price.</param>
/// <param name="Id">The order line id (F2.4): a return request names its lines by it.</param>
public sealed record OrderItemDto(Guid Id, Guid VariantId, string Sku, string Title, int Quantity, long UnitPriceMinor, long DiscountMinor, long TaxMinor, long TotalMinor,
    string? PriceSource = null, string? ContractRef = null);

/// <param name="StoreName">The seller's storefront name.</param>
public sealed record SubOrderDto(Guid Id, string SubOrderNumber, Guid SellerId, string Status, long ShippingMinor, long TotalMinor, IReadOnlyList<OrderItemDto> Items,
    string? StoreName = null);

/// <summary>
/// Where the order goes, exactly as copied into the order when it was placed (later address-book edits never change it).
/// <paramref name="SiteName"/> is set for company orders delivered to a company site. No ids, no company registration numbers.
/// </summary>
public sealed record OrderShipToDto(
    string? RecipientName, string? Line1, string? Line2, string? Upazila, string? District, string? Division, string? PostalCode, string? Phone,
    string? SiteName = null);

/// <summary>
/// What the buyer needs to know about paying: the status (<c>pending</c>, <c>captured</c>, <c>failed</c>, <c>needs_refund</c>,
/// <c>refunded</c>), the method (the payment channel name), the amount asked and when it was received. With several attempts
/// it is the money-bearing one, else the latest; <paramref name="Attempts"/> counts them all. No gateway references.
/// </summary>
public sealed record OrderPaymentDto(string Status, string? Method, long AmountMinor, DateTime? PaidAt, int Attempts);

/// <summary>A parcel on its way (F1.1, typed in by staff): courier, tracking number, optional https link to the courier's page, dates. No ids.</summary>
public sealed record OrderShipmentDto(string CourierName, string TrackingNumber, string? TrackingUrl, DateTime ShippedAtUtc, DateTime? DeliveredAtUtc);

/// <param name="QuoteNumber">The number of the quote this order accepted (ADR-0011 §11); null for an order placed from the cart.</param>
/// <param name="Shipments">Oldest first; empty until the order ships (one per order until partial shipments, F1.4).</param>
public sealed record OrderDto(
    Guid Id, string OrderNumber, string Status, string Currency, long SubtotalMinor, long DiscountMinor, long TaxMinor,
    long ShippingMinor, long GrandTotalMinor, DateTime PlacedAtUtc, IReadOnlyList<SubOrderDto> SubOrders, string? PurchaseOrderNumber = null,
    DateTime? EstimatedDeliveryBy = null, OrderShipToDto? ShipTo = null, OrderPaymentDto? Payment = null, OrderCreditDto? Credit = null,
    string? QuoteNumber = null, IReadOnlyList<OrderShipmentDto>? Shipments = null);

/// <summary>
/// Where a credit order stands in the ledger (ADR-0010 §10, B3.9): <paramref name="Status"/> = <c>open</c>, <c>partially_paid</c>, <c>paid</c>,
/// <c>overdue</c> or <c>cancelled</c>, the due date (Dhaka) fixed at the charge and what is still owed. Only on a credit order that was charged,
/// and only for a person who may see the company's credit now (a role in <c>Credit:BalanceVisibleTo</c>); derived from the ledger, never stored.
/// </summary>
public sealed record OrderCreditDto(string Status, DateOnly DueOn, long OutstandingMinor);

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
    /// The same order by its number (the one in emails). Same visibility as <see cref="GetAsync"/>; a malformed, unknown or
    /// someone else's number all answer the same "not found".
    /// </summary>
    Task<OrderDto> GetByNumberAsync(Guid userId, string orderNumber, CancellationToken ct = default);

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
    /// Accepts a sent quote version as one order of all its lines, through the same path as <see cref="PlaceAsync"/> (ADR-0011 §8.3): the
    /// idempotency record, a verified account, the open-unpaid cap, cost-centre rules, the company's approval rule on the server total, the
    /// credit check and overdue hold, spend limit and budget, stock reservation. Lines at their quoted net prices with today's VAT rate, no
    /// promotions; delivery fixed to the quoted site or calculated as for an order; advance-only terms never on credit. The quote row and its
    /// versions are locked first in the order's transaction and checked again there; one live order per version. Anything the caller may
    /// not see is one 404 ("Quote not found."), a member without the Buyer role 403 <c>COMPANY_ROLE_REQUIRED</c>.
    /// </summary>
    Task<PlacedOrder> PlaceFromQuoteAsync(AcceptQuoteRequest request, CancellationToken ct = default);

    /// <summary>
    /// The accept preview (ADR-0011 §8.4, QT15): every check and the pricing of <see cref="PlaceFromQuoteAsync"/> at one instant, with no
    /// stock held and nothing written, so the same request accepted at the same moment is charged exactly the preview's total. The key and
    /// <c>ExpectedGrandTotalMinor</c> are not read. The same refusals as acceptance (404, 403, 409 codes).
    /// </summary>
    Task<Mechware.Application.Quotes.QuoteAcceptPreviewDto> PreviewQuoteAcceptanceAsync(AcceptQuoteRequest request, CancellationToken ct = default);

    /// <summary>
    /// The shipping <see cref="PlaceAsync"/> would charge for these lines and destination, computed the same way and from the
    /// database only; holds no stock and writes no order. Refusals are the same 422 codes placement gives.
    /// </summary>
    Task<ShippingQuoteDto> QuoteShippingAsync(ShippingQuoteRequest request, CancellationToken ct = default);
}
