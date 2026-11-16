using System.Reflection;
using FluentAssertions;
using Mechware.Api.Authorization;
using Mechware.Api.Controllers;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Payments;
using Mechware.Domain.Pricing;

namespace Mechware.UnitTests.Orders;

/// <summary>E7.2: which orders staff may cancel after confirmation, the reason rule, the refund flag and who may do it.</summary>
public class ConfirmedOrderCancelTests
{
    private static readonly DateTime Now = new(2026, 11, 16, 9, 0, 0, DateTimeKind.Utc);
    private static readonly Guid Staff = Guid.NewGuid();

    private static Order PlacedOrder(Guid? company = null)
    {
        var seller = Guid.NewGuid();
        var line = new PricedLine(Guid.NewGuid(), Guid.NewGuid(), seller, "SKU-1", "Bearing", 2, 50_000, 0.15m, 100_000, 0, 100_000, 15_000, 115_000);
        var priced = new PricedOrder([new PricedSubOrder(seller, [line], 100_000, 0, 15_000, 0, 115_000, 0.05m, 5_000, 110_000)],
            100_000, 0, 15_000, 0, 115_000);
        return Order.Place(Guid.NewGuid(), OrderNumber.Generate(Now), Guid.NewGuid(), company, priced, TaxMode.Exclusive, "{}",
            OrderStatus.PendingPayment, Now, paymentDueAtUtc: Now.AddMinutes(30));
    }

    /// <summary>An order moved to <paramref name="status"/> along the lifecycle (its events cleared).</summary>
    private static Order OrderIn(OrderStatus status)
    {
        var order = PlacedOrder(Guid.NewGuid());
        if (status == OrderStatus.Cancelled) order.Transition(OrderStatus.Cancelled, null, "buyer changed their mind", Now);
        else if (status != OrderStatus.PendingPayment)
        {
            order.Confirm(new DeliveryDeadlines(Now.AddDays(5), Now.AddDays(2), true), null, "paid", Now);
            foreach (var step in new[] { OrderStatus.Processing, OrderStatus.Packed, OrderStatus.Shipped, OrderStatus.OutForDelivery, OrderStatus.Delivered })
                if (order.Status != status) order.Transition(step, null, null, Now);
        }
        order.Status.Should().Be(status);
        order.ClearDomainEvents();
        return order;
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Processing)]
    [InlineData(OrderStatus.Packed)]
    public void AnOrderThatHasNotShipped_CanBeCancelledByStaff_WithTheReasonRecorded(OrderStatus status)
    {
        var order = OrderIn(status);
        var events = order.EventSequence;

        order.CancelAfterConfirmation(Staff, "  customer called: wrong part ordered  ", Now.AddHours(1));

        order.Status.Should().Be(OrderStatus.Cancelled);
        order.SubOrders.Should().OnlyContain(s => s.Status == OrderStatus.Cancelled);
        order.EventSequence.Should().Be(events + 1);
        order.Events.Last().Should().BeEquivalentTo(new
        {
            FromStatus = status,
            ToStatus = OrderStatus.Cancelled,
            ActorId = (Guid?)Staff,
            Reason = "customer called: wrong part ordered",
            AtUtc = Now.AddHours(1)
        });
        order.DomainEvents.Should().ContainSingle().Which.Should().BeEquivalentTo(new ConfirmedOrderCancelledEvent(order.Id, order.OrderNumber,
            order.UserId, order.CompanyId, status.ToString(), Staff, "customer called: wrong part ordered"), o => o.Excluding(e => e.EventId).Excluding(e => e.OccurredOnUtc));
    }

    [Theory]
    [InlineData(OrderStatus.PendingPayment)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.OutForDelivery)]
    [InlineData(OrderStatus.Delivered)]
    [InlineData(OrderStatus.Cancelled)]
    public void AnythingElse_IsNotCancellableHere_AndNothingChanges(OrderStatus status)
    {
        var order = OrderIn(status);
        var events = order.EventSequence;

        order.Invoking(o => o.CancelAfterConfirmation(Staff, "customer asked for it", Now)).Should().Throw<ConflictException>()
            .Which.Code.Should().Be("ORDER_NOT_CANCELLABLE");

        (order.Status, order.EventSequence, order.DomainEvents.Count).Should().Be((status, events, 0));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("    ")]
    [InlineData("oops")]
    [InlineData("  ab  ")]
    public void AReason_OfFewerThanFiveCharacters_IsRefused(string? reason)
    {
        var order = OrderIn(OrderStatus.Confirmed);
        order.Invoking(o => o.CancelAfterConfirmation(Staff, reason, Now)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Order.NormaliseCancelReason(reason)).Should().Throw<ValidationException>();
        order.Status.Should().Be(OrderStatus.Confirmed);
    }

    [Fact]
    public void AReason_IsTrimmed_AndAtMost500Characters()
    {
        Order.NormaliseCancelReason("  wrong part  ").Should().Be("wrong part");
        Order.NormaliseCancelReason("12345").Should().Be("12345");
        Order.NormaliseCancelReason(new string('x', 500)).Should().HaveLength(500);
        FluentActions.Invoking(() => Order.NormaliseCancelReason(new string('x', 501))).Should().Throw<ValidationException>();
    }

    private static Payment Captured()
    {
        var p = new Payment(Guid.NewGuid(), Guid.NewGuid(), "mock", "ref-" + Guid.NewGuid().ToString("N"), 115_000, "BDT");
        p.MarkCaptured(Now);
        return p;
    }

    [Fact]
    public void ACapturedPayment_OfACancelledOrder_JoinsTheRefundQueue()
    {
        var payment = Captured();
        payment.FlagRefundForCancelledOrder("Order cancelled after payment: wrong part", Now.AddHours(1));

        payment.Should().BeEquivalentTo(new { Status = PaymentStatus.NeedsRefund, Note = "Order cancelled after payment: wrong part", UpdatedAtUtc = (DateTime?)Now.AddHours(1) });
        payment.IsRefundable.Should().BeTrue("finance can now ask for the refund");
    }

    [Fact]
    public void OnlyACapturedPayment_IsFlagged()
    {
        var pending = new Payment(Guid.NewGuid(), Guid.NewGuid(), "mock", "ref-1", 1_000, "BDT");
        pending.Invoking(p => p.FlagRefundForCancelledOrder("cancelled", Now)).Should().Throw<ConflictException>();

        var refunded = Captured();
        refunded.MarkRefunded(Now);
        refunded.Invoking(p => p.FlagRefundForCancelledOrder("cancelled", Now)).Should().Throw<ConflictException>();

        var flagged = Captured();
        flagged.FlagRefundForCancelledOrder("cancelled", Now);
        flagged.Invoking(p => p.FlagRefundForCancelledOrder("cancelled", Now)).Should().Throw<ConflictException>("never twice");
        (pending.Status, refunded.Status, flagged.Status).Should().Be((PaymentStatus.Pending, PaymentStatus.Refunded, PaymentStatus.NeedsRefund));
    }

    [Theory]
    [InlineData(Permissions.Roles.Support, true)]
    [InlineData(Permissions.Roles.Finance, true)]
    [InlineData(Permissions.Roles.Admin, true)]
    [InlineData(Permissions.Roles.Buyer, false)]
    [InlineData(Permissions.Roles.Seller, false)]
    [InlineData(Permissions.Roles.Moderator, false)]
    public void StaffWhoHandleOrders_MayCancelThem(string role, bool granted) =>
        Permissions.GetDefaultPermissionsForRole(role).Contains(Permissions.OrdersCancel).Should().Be(granted);

    [Fact]
    public void TheCancelEndpoint_NeedsItsPermission_AndAlwaysAStepUpCode()
    {
        var method = typeof(AdminOrdersController).GetMethod(nameof(AdminOrdersController.Cancel), BindingFlags.Public | BindingFlags.Instance)!;
        method.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Permission).Should().Equal(Permissions.OrdersCancel);
        method.GetCustomAttributes<RequireStepUpAttribute>().Select(a => a.Action).Should().Equal(AdminOrdersController.CancelStepUpAction);
        AdminOrdersController.CancelStepUpAction.Should().Be("order.cancel");
    }
}
