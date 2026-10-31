using FluentAssertions;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Orders;

namespace ShopVerse.UnitTests.Orders;

public class OrderStateMachineTests
{
    private static readonly Guid Order = Guid.NewGuid();
    private static readonly DateTime Now = new(2026, 11, 1, 9, 30, 0, DateTimeKind.Utc);
    private static readonly OrderStatus[] All = Enum.GetValues<OrderStatus>();

    /// <summary>The brief's lifecycle written out independently of the implementation.</summary>
    private static readonly (OrderStatus From, OrderStatus To)[] Expected =
    [
        (OrderStatus.PendingApproval, OrderStatus.PendingPayment), (OrderStatus.PendingApproval, OrderStatus.Cancelled),
        (OrderStatus.PendingPayment, OrderStatus.Confirmed), (OrderStatus.PendingPayment, OrderStatus.Cancelled),
        (OrderStatus.Confirmed, OrderStatus.Processing), (OrderStatus.Confirmed, OrderStatus.Cancelled),
        (OrderStatus.Processing, OrderStatus.Packed), (OrderStatus.Processing, OrderStatus.Cancelled),
        (OrderStatus.Packed, OrderStatus.Shipped), (OrderStatus.Packed, OrderStatus.Cancelled),
        (OrderStatus.Shipped, OrderStatus.OutForDelivery), (OrderStatus.Shipped, OrderStatus.DeliveryFailed),
        (OrderStatus.OutForDelivery, OrderStatus.Delivered), (OrderStatus.OutForDelivery, OrderStatus.DeliveryFailed),
        (OrderStatus.Delivered, OrderStatus.Completed), (OrderStatus.Delivered, OrderStatus.ReturnRequested),
        (OrderStatus.DeliveryFailed, OrderStatus.OutForDelivery), (OrderStatus.DeliveryFailed, OrderStatus.Returned),
        (OrderStatus.Returned, OrderStatus.Refunded),
        (OrderStatus.ReturnRequested, OrderStatus.ReturnApproved), (OrderStatus.ReturnRequested, OrderStatus.Delivered),
        (OrderStatus.ReturnApproved, OrderStatus.Refunded)
    ];

    [Fact]
    public void EveryPairOfStates_IsAllowedExactlyWhenTheLifecycleSaysSo()
    {
        foreach (var from in All)
            foreach (var to in All)
            {
                OrderStateMachine.CanTransition(from, to).Should().Be(Expected.Contains((from, to)), $"{from} -> {to}");
            }
    }

    [Fact]
    public void NextStates_MatchesCanTransition()
    {
        foreach (var from in All)
            OrderStateMachine.NextStates(from).Should().BeEquivalentTo(All.Where(to => OrderStateMachine.CanTransition(from, to)));
    }

    [Fact]
    public void Completed_Cancelled_AndRefunded_AreTerminal_NothingElseIs()
    {
        All.Where(OrderStateMachine.IsTerminal).Should().BeEquivalentTo([OrderStatus.Completed, OrderStatus.Cancelled, OrderStatus.Refunded]);
    }

    [Fact]
    public void TheHappyPath_ReachesCompleted_StepByStep()
    {
        OrderStatus[] path =
        [
            OrderStatus.PendingPayment, OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.Packed,
            OrderStatus.Shipped, OrderStatus.OutForDelivery, OrderStatus.Delivered, OrderStatus.Completed
        ];
        for (var i = 1; i < path.Length; i++)
            OrderStateMachine.Transition(Order, path[i - 1], path[i], null, null, Now).To.Should().Be(path[i]);
    }

    [Fact]
    public void EveryStateIsReachableFromAnOrdersStart()
    {
        var seen = new HashSet<OrderStatus> { OrderStatus.PendingApproval, OrderStatus.PendingPayment };
        var queue = new Queue<OrderStatus>(seen);
        while (queue.Count > 0)
            foreach (var next in OrderStateMachine.NextStates(queue.Dequeue()))
                if (seen.Add(next)) queue.Enqueue(next);

        seen.Should().BeEquivalentTo(All);
    }

    [Fact]
    public void ShippedOrders_CannotBeCancelled_AndFinishedOnesCannotReopen()
    {
        var shipped = () => OrderStateMachine.Transition(Order, OrderStatus.Shipped, OrderStatus.Cancelled, null, "changed my mind", Now);
        shipped.Should().Throw<ConflictException>().Which.Code.Should().Be("ORDER_INVALID_TRANSITION");

        var reopen = () => OrderStateMachine.Transition(Order, OrderStatus.Completed, OrderStatus.Processing, null, null, Now);
        reopen.Should().Throw<ConflictException>();
    }

    [Theory]
    [InlineData(OrderStatus.PendingPayment, OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Shipped, OrderStatus.DeliveryFailed)]
    [InlineData(OrderStatus.ReturnRequested, OrderStatus.ReturnApproved)]
    public void SomeMoves_NeedAReason(OrderStatus from, OrderStatus to)
    {
        foreach (var reason in new string?[] { null, "", "   " })
        {
            var act = () => OrderStateMachine.Transition(Order, from, to, null, reason, Now);
            act.Should().Throw<ValidationException>();
        }

        OrderStateMachine.Transition(Order, from, to, Guid.Empty, "  customer asked  ", Now).Reason.Should().Be("customer asked");
    }

    [Fact]
    public void TheEvent_RecordsWhoWhenAndWhy()
    {
        var actor = Guid.NewGuid();
        var e = OrderStateMachine.Transition(Order, OrderStatus.PendingPayment, OrderStatus.Confirmed, actor, null, Now);
        e.Should().Be(new OrderEvent(Order, OrderStatus.PendingPayment, OrderStatus.Confirmed, actor, null, Now));
    }

    [Fact]
    public void StockIsHeld_OnlyBeforeAnythingLeavesTheWarehouse()
    {
        All.Where(OrderStateMachine.HoldsStock).Should().BeEquivalentTo(
            [OrderStatus.PendingPayment, OrderStatus.Confirmed, OrderStatus.Processing, OrderStatus.Packed]);
    }

    [Fact]
    public void OrderNumber_HasTheBriefsFormat_AndIsDeterministicWithAFixedSource()
    {
        var n = OrderNumber.Generate(Now, _ => 0);
        n.Should().Be("MW-261101-AAAAAA");
        OrderNumber.Generate(Now, max => max - 1).Should().Be("MW-261101-777777");
        OrderNumber.IsValid(n).Should().BeTrue();
    }

    [Fact]
    public void OrderNumbers_FromTheRealRandomSource_AreValidAndVaried()
    {
        var numbers = Enumerable.Range(0, 2000).Select(_ => OrderNumber.Generate(Now)).ToList();
        numbers.Should().OnlyContain(n => OrderNumber.IsValid(n));
        numbers.Distinct().Count().Should().BeGreaterThan(1990, "32^6 values make a clash among 2000 numbers vanishingly rare");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("SV-261101-ABCDEF")]       // the old prefix from ADR-0003
    [InlineData("MW-261101-ABCDE")]        // too short
    [InlineData("MW-261101-ABCDEF1")]      // too long
    [InlineData("MW-26110A-ABCDEF")]       // letter in the date
    [InlineData("MW-261101-abcdef")]       // lower case
    [InlineData("MW-261101-ABCD18")]       // 1 and 8 are not base32
    [InlineData("MW_261101_ABCDEF")]
    public void BadOrderNumbers_AreRefused(string? value) => OrderNumber.IsValid(value).Should().BeFalse();
}
