using System.Reflection;
using FluentAssertions;
using Mechware.Api.Authorization;
using Mechware.Api.Controllers;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;

namespace Mechware.UnitTests.Orders;

/// <summary>F1.1 manual fulfilment: which status each move needs, the shipment's field rules, the events, and who may do it.</summary>
public class OrderFulfilmentTests
{
    private static readonly DateTime Now = new(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Staff = Guid.NewGuid();

    private static readonly OrderStatus[] Reachable =
    [
        OrderStatus.PendingPayment, OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.Packed, OrderStatus.Shipped,
        OrderStatus.OutForDelivery, OrderStatus.Delivered, OrderStatus.Cancelled
    ];

    private static Order OrderIn(OrderStatus status)
    {
        var seller = Guid.NewGuid();
        var line = new PricedLine(Guid.NewGuid(), Guid.NewGuid(), seller, "SKU-1", "Bearing", 2, 50_000, 0.15m, 100_000, 0, 100_000, 15_000, 115_000);
        var priced = new PricedOrder([new PricedSubOrder(seller, [line], 100_000, 0, 15_000, 0, 115_000, 0.05m, 5_000, 110_000)],
            100_000, 0, 15_000, 0, 115_000);
        var order = Order.Place(Guid.NewGuid(), OrderNumber.Generate(Now), Guid.NewGuid(), null, priced, TaxMode.Exclusive, "{}",
            OrderStatus.PendingPayment, Now, paymentDueAtUtc: Now.AddMinutes(30));
        if (status == OrderStatus.Cancelled) order.Transition(OrderStatus.Cancelled, null, "buyer changed their mind", Now);
        else if (status != OrderStatus.PendingPayment)
        {
            order.Confirm(new DeliveryDeadlines(Now.AddDays(5), Now.AddDays(2), true), null, "paid", Now);
            foreach (var step in new[] { OrderStatus.Processing, OrderStatus.Packed, OrderStatus.Shipped, OrderStatus.OutForDelivery, OrderStatus.Delivered })
                if (order.Status != status) order.Transition(step, null, null, Now);
        }
        order.ClearDomainEvents();
        return order;
    }

    private static readonly (string Name, OrderStatus To, OrderStatus[] From, Action<Order> Move)[] Moves =
    [
        ("processing", OrderStatus.Processing, [OrderStatus.Confirmed], o => o.StartProcessing(Staff, Now)),
        ("packed", OrderStatus.Packed, [OrderStatus.Processing], o => o.MarkPacked(Staff, Now)),
        ("ship", OrderStatus.Shipped, [OrderStatus.Packed], o => o.Ship("Steadfast", "SF-123", null, Staff, Now)),
        ("delivered", OrderStatus.Delivered, [OrderStatus.Shipped, OrderStatus.OutForDelivery], o => o.MarkDelivered(null, Staff, Now))
    ];

    [Fact]
    public void EachMove_WorksOnlyFromItsStatus_AndRaisesOneEvent_ElseIs409AndChangesNothing()
    {
        foreach (var (name, to, allowed, move) in Moves)
            foreach (var from in Reachable)
            {
                var order = OrderIn(from);
                var events = order.Events.Count;
                if (allowed.Contains(from))
                {
                    move(order);
                    (order.Status, order.SubOrders.Single().Status).Should().Be((to, to), $"{name} from {from}");
                    order.Events.Count.Should().Be(events + 1);
                    order.Events.Last().ActorId.Should().Be(Staff);
                    order.DomainEvents.Should().ContainSingle($"{name} from {from}");
                }
                else
                {
                    move.Invoking(m => m(order)).Should().Throw<ConflictException>($"{name} from {from}")
                        .Which.Code.Should().Be("ORDER_INVALID_TRANSITION");
                    (order.Status, order.Events.Count, order.DomainEvents.Count).Should().Be((from, events, 0), $"{name} from {from}");
                }
            }
    }

    [Fact]
    public void Ship_StoresTheCleanedFields_AndTheShippedEventCarriesThem()
    {
        var order = OrderIn(OrderStatus.Packed);
        var shipment = order.Ship("  Steadfast​ ‮express\n Courier ", " SF-12a ", " https://www.steadfast.com.bd/t/SF-12a ", Staff, Now);

        (shipment.OrderId, shipment.CourierName, shipment.TrackingNumber, shipment.TrackingUrl, shipment.ShippedAtUtc, shipment.CreatedBy)
            .Should().Be((order.Id, "Steadfast express Courier", "SF-12a", "https://www.steadfast.com.bd/t/SF-12a", Now, Staff));
        order.DomainEvents.Single().Should().BeOfType<OrderShippedEvent>()
            .Which.Should().Match<OrderShippedEvent>(e => e.ShipmentId == shipment.Id && e.TrackingNumber == "SF-12a" && e.OrderNumber == order.OrderNumber);
        order.Events.Last().Reason.Should().Be("Shipped with Steadfast express Courier, tracking SF-12a");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("A")]
    [InlineData("  ​ B ​ ")]
    [InlineData("81")]
    public void Ship_RefusesABadCourierName(string? name)
    {
        if (name == "81") name = new string('x', 81);
        var order = OrderIn(OrderStatus.Packed);
        order.Invoking(o => o.Ship(name, "SF-1", null, Staff, Now)).Should().Throw<ValidationException>();
        order.Status.Should().Be(OrderStatus.Packed);
        Shipment.NormaliseCourierName(new string('x', 80)).Should().HaveLength(80);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SF 12")]
    [InlineData("SF_12")]
    [InlineData("SF/12")]
    [InlineData("ÄB12")]
    [InlineData("<script>")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901")]
    public void Ship_RefusesABadTrackingNumber(string? number)
    {
        var order = OrderIn(OrderStatus.Packed);
        order.Invoking(o => o.Ship("Pathao", number, null, Staff, Now)).Should().Throw<ValidationException>();
        Shipment.NormaliseTrackingNumber(new string('A', 60)).Should().HaveLength(60);
    }

    [Theory]
    [InlineData("http://pathao.com/track/1")]
    [InlineData("https://evil.example/track")]
    [InlineData("https://pathao.com.evil.example/track")]
    [InlineData("https://evilpathao.com/track")]
    [InlineData("https://user:pw@pathao.com/track")]
    [InlineData("https://pathao.com:8443/track")]
    [InlineData("javascript:alert(1)")]
    [InlineData("//pathao.com/track")]
    [InlineData("pathao.com/track")]
    public void Ship_RefusesATrackingLinkThatIsNotHttpsOnAKnownCourierSite(string url)
    {
        var order = OrderIn(OrderStatus.Packed);
        order.Invoking(o => o.Ship("Pathao", "PA-1", url, Staff, Now)).Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("trackingUrl");
    }

    [Fact]
    public void ATrackingLink_IsOptional_AndAllowedOnEveryListedHostAndItsSubdomains()
    {
        Shipment.NormaliseTrackingUrl("   ").Should().BeNull();
        foreach (var host in Shipment.TrackingUrlHosts)
        {
            Shipment.NormaliseTrackingUrl($"https://{host}/t?id=1").Should().Be($"https://{host}/t?id=1");
            Shipment.NormaliseTrackingUrl($"https://track.{host.ToUpperInvariant()}/x").Should().Be($"https://track.{host}/x");
        }
    }

    [Fact]
    public void Delivered_StampsTheShipment_AndAShipmentOfAnotherOrderIsRefused()
    {
        var packed = OrderIn(OrderStatus.Packed);
        var shipment = packed.Ship("Pathao", "PA-1", null, Staff, Now);
        packed.ClearDomainEvents();
        packed.MarkDelivered(shipment, Staff, Now.AddDays(1));
        (packed.Status, shipment.DeliveredAtUtc).Should().Be((OrderStatus.Delivered, Now.AddDays(1)));
        packed.DomainEvents.Single().Should().BeOfType<OrderDeliveredEvent>().Which.ShipmentId.Should().Be(shipment.Id);

        var other = OrderIn(OrderStatus.Shipped);
        other.Invoking(o => o.MarkDelivered(shipment, Staff, Now)).Should().Throw<InvalidOperationException>();
        other.Status.Should().Be(OrderStatus.Shipped);
    }

    [Theory]
    [InlineData(Permissions.Roles.Support, true)]
    [InlineData(Permissions.Roles.Admin, true)]
    [InlineData(Permissions.Roles.Finance, false)]
    [InlineData(Permissions.Roles.Moderator, false)]
    [InlineData(Permissions.Roles.Seller, false)]
    [InlineData(Permissions.Roles.Buyer, false)]
    public void OnlySupportAndAdmin_MayFulfil(string role, bool granted) =>
        Permissions.GetDefaultPermissionsForRole(role).Contains(Permissions.OrdersFulfil).Should().Be(granted);

    [Theory]
    [InlineData(nameof(AdminOrdersController.Processing))]
    [InlineData(nameof(AdminOrdersController.Packed))]
    [InlineData(nameof(AdminOrdersController.Ship))]
    [InlineData(nameof(AdminOrdersController.Delivered))]
    public void EveryFulfilmentEndpoint_NeedsOrdersFulfil(string action) =>
        typeof(AdminOrdersController).GetMethod(action)!.GetCustomAttribute<RequirePermissionAttribute>()!.Permission.Should().Be(Permissions.OrdersFulfil);
}
