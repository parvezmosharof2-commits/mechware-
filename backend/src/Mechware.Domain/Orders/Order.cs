using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;
using Mechware.Domain.Pricing;
using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.Domain.Orders;

/// <summary>Published through the outbox when an order is placed; notifications, search sync and ledgers hang off it.</summary>
public sealed record OrderPlacedEvent(Guid OrderId, string OrderNumber, Guid UserId, long GrandTotalMinor) : DomainEventBase;

/// <summary>Staff cancelled an order after it was confirmed (E7.2): stock goes back, credit is released, paid money is owed back.</summary>
public sealed record ConfirmedOrderCancelledEvent(
    Guid OrderId, string OrderNumber, Guid UserId, Guid? CompanyId, string FromStatus, Guid ActorId, string Reason) : DomainEventBase;

/// <summary>One purchased line, frozen at the moment of purchase: later catalog edits never change what was sold.</summary>
public sealed class OrderItem : Entity<Guid>
{
    public Guid SubOrderId { get; private set; }
    public Guid VariantId { get; private set; }
    public string Sku { get; private set; } = string.Empty;
    public string Title { get; private set; } = string.Empty;
    public int Quantity { get; private set; }
    public long UnitPriceMinor { get; private set; }
    public decimal VatRate { get; private set; }
    public long GrossMinor { get; private set; }
    public long DiscountMinor { get; private set; }
    public long NetMinor { get; private set; }
    public long TaxMinor { get; private set; }
    public long TotalMinor { get; private set; }

    // Price snapshot (ADR-0008 §8): where the unit price came from at the order's instant. Null on lines placed before D2.5,
    // except the basis, which the migration filled in from the order's tax mode. Nothing reads a price list for a placed order.
    public PriceSource? PriceSource { get; private set; }
    public PriceBasis? PriceBasis { get; private set; }
    public Guid? PriceListId { get; private set; }
    public int? PriceListVersion { get; private set; }
    public Guid? PriceListEntryId { get; private set; }

    /// <summary>The contract number, for a <see cref="Pricing.PriceLists.PriceSource.Contract"/> price only.</summary>
    public string? ContractRef { get; private set; }

    /// <summary>The lowest public price (list, sale, public break) for this quantity at that instant, on <see cref="PriceBasis"/>.</summary>
    public long? PublicUnitPriceMinor { get; private set; }

    /// <summary>The accepted quote line, for a <see cref="Pricing.PriceLists.PriceSource.Quote"/> price only (ADR-0011 §11).</summary>
    public Guid? QuoteLineId { get; private set; }

    private OrderItem() { }

    internal OrderItem(Guid id, Guid subOrderId, PricedLine line) : base(id)
    {
        if (line.Price is { } price)
        {
            (PriceSource, PriceBasis, PriceListId, PriceListVersion, PriceListEntryId, ContractRef, PublicUnitPriceMinor) =
                (price.Source, price.Basis, price.PriceListId, price.PriceListVersion, price.EntryId, price.ContractRef, price.PublicUnitPriceMinor);
        }

        QuoteLineId = line.QuoteLineId;
        SubOrderId = subOrderId;
        VariantId = line.VariantId;
        Sku = line.Sku;
        Title = line.Title;
        Quantity = line.Quantity;
        UnitPriceMinor = line.UnitPriceMinor;
        VatRate = line.VatRate;
        GrossMinor = line.GrossMinor;
        DiscountMinor = line.DiscountMinor;
        NetMinor = line.NetMinor;
        TaxMinor = line.TaxMinor;
        TotalMinor = line.TotalMinor;
    }
}

/// <summary>The part of an order one seller fulfils and is paid for (ADR-0003).</summary>
public sealed class SubOrder : Entity<Guid>
{
    private readonly List<OrderItem> _items = [];

    public Guid OrderId { get; private set; }
    public Guid SellerId { get; private set; }
    public string SubOrderNumber { get; private set; } = string.Empty;
    public OrderStatus Status { get; private set; }
    public long GoodsNetMinor { get; private set; }
    public long DiscountMinor { get; private set; }
    public long TaxMinor { get; private set; }
    public long ShippingMinor { get; private set; }
    public long TotalMinor { get; private set; }
    public decimal CommissionRate { get; private set; }
    public long CommissionMinor { get; private set; }
    public long SellerEarningMinor { get; private set; }

    public IReadOnlyCollection<OrderItem> Items => _items.AsReadOnly();

    private SubOrder() { }

    internal SubOrder(Guid id, Guid orderId, string subOrderNumber, OrderStatus status, PricedSubOrder priced) : base(id)
    {
        OrderId = orderId;
        SellerId = priced.SellerId;
        SubOrderNumber = subOrderNumber;
        Status = status;
        GoodsNetMinor = priced.GoodsNetMinor;
        DiscountMinor = priced.DiscountMinor;
        TaxMinor = priced.TaxMinor;
        ShippingMinor = priced.ShippingMinor;
        TotalMinor = priced.TotalMinor;
        CommissionRate = priced.CommissionRate;
        CommissionMinor = priced.CommissionMinor;
        SellerEarningMinor = priced.SellerEarningMinor;
        foreach (var line in priced.Lines) _items.Add(new OrderItem(Guid.NewGuid(), id, line));
    }

    internal void SetStatus(OrderStatus status) => Status = status;
}

/// <summary>Every state change of an order, recorded in the same transaction as the change.</summary>
public sealed class OrderEventRecord : Entity<Guid>
{
    public Guid OrderId { get; private set; }

    /// <summary>1, 2, 3 ... per order. Timestamps can tie (two changes in one request); this never does.</summary>
    public int Sequence { get; private set; }
    public OrderStatus FromStatus { get; private set; }
    public OrderStatus ToStatus { get; private set; }
    public Guid? ActorId { get; private set; }
    public string? Reason { get; private set; }
    public DateTime AtUtc { get; private set; }

    private OrderEventRecord() { }

    internal OrderEventRecord(OrderEvent e, int sequence) : base(Guid.NewGuid())
    {
        OrderId = e.OrderId;
        Sequence = sequence;
        FromStatus = e.From;
        ToStatus = e.To;
        ActorId = e.ActorId;
        Reason = e.Reason;
        AtUtc = e.AtUtc;
    }
}

/// <summary>
/// The customer-facing order: one per checkout, split into one sub-order per seller. All money is the server's own
/// computation (<see cref="OrderPricing"/>); the state only changes through <see cref="OrderStateMachine"/>.
/// </summary>
public sealed class Order : AggregateRoot<Guid>
{
    private readonly List<SubOrder> _subOrders = [];
    private readonly List<OrderEventRecord> _events = [];

    public string OrderNumber { get; private set; } = string.Empty;
    public Guid UserId { get; private set; }
    public Guid? CompanyId { get; private set; }
    public OrderStatus Status { get; private set; }
    public TaxMode TaxMode { get; private set; }
    public string Currency { get; private set; } = "BDT";
    public long SubtotalMinor { get; private set; }
    public long DiscountMinor { get; private set; }
    public long TaxMinor { get; private set; }
    public long ShippingMinor { get; private set; }
    public long GrandTotalMinor { get; private set; }
    public string ShipToJson { get; private set; } = "{}";

    /// <summary>How many events this order has had. Kept on the order itself so a change never has to load the event list.</summary>
    public int EventSequence { get; private set; }
    public DateTime PlacedAtUtc { get; private set; }

    /// <summary>The buyer chose company credit terms; when an approval is needed the credit is held only once it is given.</summary>
    public bool PaysOnCredit { get; private set; }

    /// <summary>The company cost centre the order is charged to, if any; its monthly budget counts this order.</summary>
    public Guid? CostCentreId { get; private set; }

    /// <summary>
    /// When an order waiting for payment is cancelled if still unpaid. Starts when the order becomes payable: at placement, or
    /// when its approval is given (threat model v1, H1), so a long approval does not eat the buyer's time to pay.
    /// </summary>
    public DateTime? PaymentDueAtUtc { get; private set; }

    /// <summary>The company's own purchase order number (company orders only), as normalised by <see cref="NormalisePurchaseOrderNumber"/>.</summary>
    public string? PurchaseOrderNumber { get; private set; }

    public const int PurchaseOrderNumberMaxLength = 40;

    /// <summary>
    /// Trims the buyer's PO number; blank means none. 1 to 40 characters of ASCII letters, digits, space and <c>- _ . # /</c>,
    /// so it can never carry control characters, markup or formula prefixes into invoices, e-mails or CSV exports.
    /// </summary>
    public static string? NormalisePurchaseOrderNumber(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        if (trimmed.Length > PurchaseOrderNumberMaxLength || !trimmed.All(c => char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.' or '#' or '/'))
            throw new ValidationException("purchaseOrderNumber",
                $"A purchase order number is at most {PurchaseOrderNumberMaxLength} letters, digits, spaces or - _ . # /.");
        return trimmed;
    }

    /// <summary>When the order became confirmed (paid, or accepted on credit terms). Null until then and on orders confirmed before E8.3.</summary>
    public DateTime? ConfirmedAtUtc { get; private set; }

    /// <summary>The legal delivery deadline (E8.3), set once at confirmation by <see cref="Confirm"/>; never client-supplied, never recomputed. Order level: sub-orders do not ship on separate dates yet.</summary>
    public DateTime? LegalDeliveryDeadlineUtc { get; private set; }

    /// <summary>The courier handover deadline (E8.3), set with <see cref="LegalDeliveryDeadlineUtc"/>.</summary>
    public DateTime? CourierHandoverDeadlineUtc { get; private set; }

    /// <summary>Whether the same-city period applied (true), the other-city one (false), or the destination or dispatch district was unknown (null, short period used).</summary>
    public bool? DeadlineSameCity { get; private set; }

    /// <summary>
    /// The quote and the version this order accepted (ADR-0011 §8, §11): both or neither, a company order whose every line is a quote
    /// line, set at placement and never changed. Whether the quote may be accepted again is derived from this order's status.
    /// </summary>
    public Guid? QuoteId { get; private set; }

    public Guid? QuoteVersionId { get; private set; }

    public IReadOnlyCollection<SubOrder> SubOrders => _subOrders.AsReadOnly();
    public IReadOnlyCollection<OrderEventRecord> Events => _events.AsReadOnly();

    private Order() { }

    public static Order Place(
        Guid id, string orderNumber, Guid userId, Guid? companyId, PricedOrder priced, TaxMode taxMode, string shipToJson,
        OrderStatus initialStatus, DateTime nowUtc, bool paysOnCredit = false, DateTime? paymentDueAtUtc = null, Guid? costCentreId = null,
        string? purchaseOrderNumber = null, Guid? quoteId = null, Guid? quoteVersionId = null)
    {
        var po = NormalisePurchaseOrderNumber(purchaseOrderNumber);
        if (po is not null && companyId is null)
            throw new ValidationException("purchaseOrderNumber", "A purchase order number applies to company orders only.");
        if ((quoteId is null) != (quoteVersionId is null) || (quoteId is not null && companyId is null)
            || priced.SubOrders.SelectMany(s => s.Lines).Any(l => (l.QuoteLineId is null) != (quoteId is null)))
            throw new ValidationException("quote", "A quote order is a company order of the quote's version, and every line of it is a quote line.");
        if (!OrderNumber_IsValid(orderNumber)) throw new ValidationException("orderNumber", "Invalid order number.");
        if (initialStatus is not (OrderStatus.PendingPayment or OrderStatus.PendingApproval))
            throw new ValidationException("status", "An order starts pending payment or pending approval.");
        if (priced.SubOrders.Count == 0) throw new ValidationException("lines", "An order needs at least one line.");

        var order = new Order(id)
        {
            OrderNumber = orderNumber,
            UserId = userId,
            CompanyId = companyId,
            Status = initialStatus,
            TaxMode = taxMode,
            SubtotalMinor = priced.SubtotalMinor,
            DiscountMinor = priced.DiscountMinor,
            TaxMinor = priced.TaxMinor,
            ShippingMinor = priced.ShippingMinor,
            GrandTotalMinor = priced.GrandTotalMinor,
            ShipToJson = string.IsNullOrWhiteSpace(shipToJson) ? "{}" : shipToJson,
            PlacedAtUtc = nowUtc,
            PaysOnCredit = paysOnCredit,
            CostCentreId = costCentreId,
            PurchaseOrderNumber = po,
            PaymentDueAtUtc = initialStatus == OrderStatus.PendingPayment ? paymentDueAtUtc : null,
            QuoteId = quoteId,
            QuoteVersionId = quoteVersionId
        };

        var n = 0;
        foreach (var sub in priced.SubOrders)
        {
            n++;
            order._subOrders.Add(new SubOrder(Guid.NewGuid(), id, $"{orderNumber}-{n:D2}", initialStatus, sub));
        }

        order._events.Add(new OrderEventRecord(new OrderEvent(id, initialStatus, initialStatus, userId, "Order placed", nowUtc), ++order.EventSequence));
        order.AddDomainEvent(new OrderPlacedEvent(id, orderNumber, userId, priced.GrandTotalMinor));
        return order;
    }

    /// <summary>The only way the state changes: validated, recorded, and applied to every sub-order.</summary>
    public void Transition(OrderStatus to, Guid? actorId, string? reason, DateTime nowUtc)
    {
        if (to == OrderStatus.Confirmed)
            throw new InvalidOperationException("An order is confirmed through Confirm, which sets its delivery deadlines.");
        Apply(to, actorId, reason, nowUtc);
    }

    /// <summary>Confirms the order (paid, or accepted on credit) and fixes its delivery deadlines in the same step, once.</summary>
    public void Confirm(DeliveryDeadlines deadlines, Guid? actorId, string? reason, DateTime nowUtc)
    {
        Apply(OrderStatus.Confirmed, actorId, reason, nowUtc);
        ConfirmedAtUtc = nowUtc;
        LegalDeliveryDeadlineUtc = deadlines.LegalDeliveryDeadlineUtc;
        CourierHandoverDeadlineUtc = deadlines.CourierHandoverDeadlineUtc;
        DeadlineSameCity = deadlines.SameCity;
    }

    private void Apply(OrderStatus to, Guid? actorId, string? reason, DateTime nowUtc)
    {
        var e = OrderStateMachine.Transition(Id, Status, to, actorId, reason, nowUtc);
        Status = to;
        foreach (var sub in _subOrders) sub.SetStatus(to);
        _events.Add(new OrderEventRecord(e, ++EventSequence));
        UpdatedAtUtc = nowUtc;
    }

    public const int CancelReasonMinLength = 5;
    public const int CancelReasonMaxLength = 500;

    /// <summary>The trimmed reason for a staff cancel: 5 to 500 characters.</summary>
    public static string NormaliseCancelReason(string? reason)
    {
        var trimmed = reason?.Trim() ?? string.Empty;
        if (trimmed.Length is < CancelReasonMinLength or > CancelReasonMaxLength)
            throw new ValidationException("reason", $"Give the reason for the cancellation ({CancelReasonMinLength} to {CancelReasonMaxLength} characters).");
        return trimmed;
    }

    /// <summary>
    /// Staff cancel an order whose stock was already deducted but has not left the warehouse (Confirmed, Processing, Packed; E7.2).
    /// Anything else is 409 <c>ORDER_NOT_CANCELLABLE</c>: a pending order is cancelled by its buyer or its expiry, a shipped one goes
    /// through a return (F2). Raises <see cref="ConfirmedOrderCancelledEvent"/>.
    /// </summary>
    public void CancelAfterConfirmation(Guid actorId, string? reason, DateTime nowUtc)
    {
        var why = NormaliseCancelReason(reason);
        var from = Status;
        if (from is not (OrderStatus.Confirmed or OrderStatus.Processing or OrderStatus.Packed))
            throw new ConflictException("ORDER_NOT_CANCELLABLE", $"Only a confirmed order that has not shipped can be cancelled here; this one is {from}.");
        Apply(OrderStatus.Cancelled, actorId, why, nowUtc);
        AddDomainEvent(new ConfirmedOrderCancelledEvent(Id, OrderNumber, UserId, CompanyId, from.ToString(), actorId, why));
    }

    // Manual fulfilment (F1.1). Each step goes through the state machine (409 ORDER_INVALID_TRANSITION from the wrong status), so it
    // inherits the payment rule: only Confirm (captured payment or accepted credit) reaches Confirmed, the only way into Processing.

    /// <summary>Confirmed → Processing.</summary>
    public void StartProcessing(Guid actorId, DateTime nowUtc)
    {
        Apply(OrderStatus.Processing, actorId, null, nowUtc);
        AddDomainEvent(new OrderProcessingStartedEvent(Id, OrderNumber, UserId, actorId));
    }

    /// <summary>Processing → Packed.</summary>
    public void MarkPacked(Guid actorId, DateTime nowUtc)
    {
        Apply(OrderStatus.Packed, actorId, null, nowUtc);
        AddDomainEvent(new OrderPackedEvent(Id, OrderNumber, UserId, actorId));
    }

    /// <summary>Packed → Shipped with the courier and tracking number typed in by staff (validated first: 400 before any state check).</summary>
    public Shipment Ship(string? courierName, string? trackingNumber, string? trackingUrl, Guid actorId, DateTime nowUtc)
    {
        var shipment = new Shipment(Id, Shipment.NormaliseCourierName(courierName), Shipment.NormaliseTrackingNumber(trackingNumber),
            Shipment.NormaliseTrackingUrl(trackingUrl), actorId, nowUtc);
        Apply(OrderStatus.Shipped, actorId, $"Shipped with {shipment.CourierName}, tracking {shipment.TrackingNumber}", nowUtc);
        AddDomainEvent(new OrderShippedEvent(Id, OrderNumber, UserId, shipment.Id, shipment.CourierName, shipment.TrackingNumber,
            shipment.TrackingUrl, actorId));
        return shipment;
    }

    /// <summary>Shipped (or OutForDelivery) → Delivered; the order's shipment, if any, gets its delivery time.</summary>
    public void MarkDelivered(Shipment? shipment, Guid actorId, DateTime nowUtc)
    {
        if (shipment is not null && shipment.OrderId != Id) throw new InvalidOperationException("The shipment belongs to another order.");
        Apply(OrderStatus.Delivered, actorId, null, nowUtc);
        shipment?.MarkDelivered(nowUtc);
        AddDomainEvent(new OrderDeliveredEvent(Id, OrderNumber, UserId, shipment?.Id, actorId));
    }

    /// <summary>The approval was given: the order can now be paid, and the time to pay starts now.</summary>
    public void Approve(Guid? actorId, DateTime paymentDueAtUtc, DateTime nowUtc)
    {
        Transition(OrderStatus.PendingPayment, actorId, "Approved by the company", nowUtc);
        PaymentDueAtUtc = paymentDueAtUtc;
    }

    private Order(Guid id) : base(id) { }

    private static bool OrderNumber_IsValid(string value) => Orders.OrderNumber.IsValid(value);
}
