using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;
using ShopVerse.Domain.Pricing;

namespace ShopVerse.Domain.Orders;

/// <summary>Published through the outbox when an order is placed; notifications, search sync and ledgers hang off it.</summary>
public sealed record OrderPlacedEvent(Guid OrderId, string OrderNumber, Guid UserId, long GrandTotalMinor) : DomainEventBase;

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

    private OrderItem() { }

    internal OrderItem(Guid id, Guid subOrderId, PricedLine line) : base(id)
    {
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
    public OrderStatus FromStatus { get; private set; }
    public OrderStatus ToStatus { get; private set; }
    public Guid? ActorId { get; private set; }
    public string? Reason { get; private set; }
    public DateTime AtUtc { get; private set; }

    private OrderEventRecord() { }

    internal OrderEventRecord(OrderEvent e) : base(Guid.NewGuid())
    {
        OrderId = e.OrderId;
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
    public DateTime PlacedAtUtc { get; private set; }

    public IReadOnlyCollection<SubOrder> SubOrders => _subOrders.AsReadOnly();
    public IReadOnlyCollection<OrderEventRecord> Events => _events.AsReadOnly();

    private Order() { }

    public static Order Place(
        Guid id, string orderNumber, Guid userId, Guid? companyId, PricedOrder priced, TaxMode taxMode, string shipToJson,
        OrderStatus initialStatus, DateTime nowUtc)
    {
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
            PlacedAtUtc = nowUtc
        };

        var n = 0;
        foreach (var sub in priced.SubOrders)
        {
            n++;
            order._subOrders.Add(new SubOrder(Guid.NewGuid(), id, $"{orderNumber}-{n:D2}", initialStatus, sub));
        }

        order._events.Add(new OrderEventRecord(new OrderEvent(id, initialStatus, initialStatus, userId, "Order placed", nowUtc)));
        order.AddDomainEvent(new OrderPlacedEvent(id, orderNumber, userId, priced.GrandTotalMinor));
        return order;
    }

    /// <summary>The only way the state changes: validated, recorded, and applied to every sub-order.</summary>
    public void Transition(OrderStatus to, Guid? actorId, string? reason, DateTime nowUtc)
    {
        var e = OrderStateMachine.Transition(Id, Status, to, actorId, reason, nowUtc);
        Status = to;
        foreach (var sub in _subOrders) sub.SetStatus(to);
        _events.Add(new OrderEventRecord(e));
        UpdatedAtUtc = nowUtc;
    }

    private Order(Guid id) : base(id) { }

    private static bool OrderNumber_IsValid(string value) => Orders.OrderNumber.IsValid(value);
}
