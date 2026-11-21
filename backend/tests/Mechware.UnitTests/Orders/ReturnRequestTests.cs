using FluentAssertions;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Returns;

namespace Mechware.UnitTests.Orders;

/// <summary>F2.1 return request rules: window on Dhaka calendar days, delivered order only, quantities, reasons, note, withdraw.</summary>
public class ReturnRequestTests
{
    private static readonly Guid Line1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Line2 = Guid.Parse("00000000-0000-0000-0000-000000000002");

    // Delivered 2026-11-01 23:30 Dhaka (17:30 UTC). Dhaka delivery day = 1 Nov; 7 days → open until 8 Nov 23:59:59 Dhaka = 8 Nov 17:59:59 UTC.
    private static readonly DateTime Delivered = new(2026, 11, 1, 17, 30, 0, DateTimeKind.Utc);

    private static ReturnRequest Open(
        DateTime now, OrderStatus status = OrderStatus.Delivered, DateTime? delivered = null, int qty = 1, int claimed = 0, bool returnable = true,
        ReturnReason reason = ReturnReason.Damaged, Guid? line = null) =>
        ReturnRequest.Open(Guid.NewGuid(), "MW-261101-ABC123", status, delivered ?? Delivered, null, Guid.NewGuid(), reason, null,
            [new ReturnLineRequest(line ?? Line1, qty)],
            [new ReturnableLine(Line1, 3, claimed, returnable), new ReturnableLine(Line2, 1, 0, true)], 7, now, "key-12345678", new string('a', 64));

    [Fact]
    public void Window_EndsAtDhakaMidnight_AfterTheSeventhCalendarDay_WhateverTheDeliveryHour()
    {
        ReturnRequest.WindowEndsUtc(Delivered, 7).Should().Be(new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc));
        // 00:10 Dhaka on 2 Nov is still 1 Nov in UTC; its Dhaka day is the 2nd, so the window runs one Dhaka day longer.
        ReturnRequest.WindowEndsUtc(new DateTime(2026, 11, 1, 18, 10, 0, DateTimeKind.Utc), 7).Should().Be(new DateTime(2026, 11, 9, 18, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void AReturn_OneSecondBeforeDhakaMidnight_IsOpened_AtMidnightItIsRefused()
    {
        Open(new DateTime(2026, 11, 8, 17, 59, 59, DateTimeKind.Utc)).Status.Should().Be(ReturnStatus.Requested);
        FluentActions.Invoking(() => Open(new DateTime(2026, 11, 8, 18, 0, 0, DateTimeKind.Utc)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("RETURN_WINDOW_CLOSED");
    }

    [Theory]
    [InlineData(OrderStatus.Confirmed)]
    [InlineData(OrderStatus.Shipped)]
    [InlineData(OrderStatus.OutForDelivery)]
    [InlineData(OrderStatus.Cancelled)]
    [InlineData(OrderStatus.Completed)]
    public void AnOrderThatIsNotDelivered_CannotBeReturned(OrderStatus status) =>
        FluentActions.Invoking(() => Open(Delivered.AddHours(1), status))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("RETURN_ORDER_NOT_DELIVERED");

    [Fact]
    public void ADeliveredOrderWithoutADeliveryTime_CannotBeReturned() =>
        FluentActions.Invoking(() => ReturnRequest.Open(Guid.NewGuid(), "MW-261101-ABC123", OrderStatus.Delivered, null, null, Guid.NewGuid(),
                ReturnReason.Damaged, null, [new ReturnLineRequest(Line1, 1)], [new ReturnableLine(Line1, 1, 0, true)], 7, Delivered, "key-12345678", new string('a', 64)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("RETURN_ORDER_NOT_DELIVERED");

    [Theory]
    [InlineData(3, 0, true)]
    [InlineData(4, 0, false)]
    [InlineData(1, 2, true)]
    [InlineData(2, 2, false)]
    [InlineData(1, 3, false)]
    public void Quantity_IsAtMostDeliveredMinusWhatOtherOpenReturnsClaim(int qty, int claimed, bool allowed)
    {
        var act = () => Open(Delivered.AddDays(1), qty: qty, claimed: claimed);
        if (allowed) act.Should().NotThrow();
        else act.Should().Throw<ConflictException>().Which.Code.Should().Be("RETURN_QUANTITY_EXCEEDED");
    }

    [Fact]
    public void ALineOfAnotherOrder_Is400_AndANonReturnableLine_Is422()
    {
        FluentActions.Invoking(() => Open(Delivered.AddDays(1), line: Guid.NewGuid())).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => Open(Delivered.AddDays(1), returnable: false))
            .Should().Throw<UnprocessableException>().Which.Code.Should().Be("RETURN_LINE_NOT_RETURNABLE");
    }

    [Fact]
    public void Lines_AreOneToAHundred_EachOnce_EachQuantityOneToTenThousand()
    {
        FluentActions.Invoking(() => ReturnRequest.NormaliseLines(null)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseLines([])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseLines([new(Line1, 1), new(Line1, 2)])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseLines([new(Line1, 0)])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseLines([new(Line1, -1)])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseLines([new(Line1, 10_001)])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseLines([new(Guid.Empty, 1)])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseLines([.. Enumerable.Range(0, 101).Select(_ => new ReturnLineRequest(Guid.NewGuid(), 1))]))
            .Should().Throw<ValidationException>();
        ReturnRequest.NormaliseLines([new(Line2, 1), new(Line1, 10_000)]).Select(l => l.OrderItemId).Should().Equal(Line1, Line2);
    }

    [Theory]
    [InlineData("damaged", ReturnReason.Damaged)]
    [InlineData(" WRONG_ITEM ", ReturnReason.WrongItem)]
    [InlineData("not_as_described", ReturnReason.NotAsDescribed)]
    [InlineData("other", ReturnReason.Other)]
    public void Reasons_AreTheFourCodes(string code, ReturnReason expected) => ReturnRequest.ParseReason(code).Should().Be(expected);

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("broken")]
    [InlineData("1")]
    public void AnyOtherReason_Is400(string? code) =>
        FluentActions.Invoking(() => ReturnRequest.ParseReason(code)).Should().Throw<ValidationException>();

    [Fact]
    public void Note_IsPlainTextTrimmedAtMost1000_AndRequiredForOther()
    {
        ReturnRequest.NormaliseNote("  Box crushed​‮  ", ReturnReason.Damaged).Should().Be("Box crushed");
        ReturnRequest.NormaliseNote("   ", ReturnReason.Damaged).Should().BeNull();
        ReturnRequest.NormaliseNote(new string('x', 1000), ReturnReason.Damaged).Should().HaveLength(1000);
        FluentActions.Invoking(() => ReturnRequest.NormaliseNote(new string('x', 1001), ReturnReason.Damaged)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseNote(" ", ReturnReason.Other)).Should().Throw<ValidationException>();
        ReturnRequest.NormaliseNote("Wrong voltage", ReturnReason.Other).Should().Be("Wrong voltage");
    }

    [Fact]
    public void Opening_RaisesReturnRequested_WithdrawingRaisesReturnWithdrawnOnce_AndAHandledReturnCannotBeWithdrawn()
    {
        var r = Open(Delivered.AddDays(1), qty: 2);
        r.DomainEvents.OfType<ReturnRequestedEvent>().Single().Quantity.Should().Be(2);
        r.Lines.Single().Quantity.Should().Be(2);

        r.Withdraw("MW-261101-ABC123", Delivered.AddDays(2)).Should().BeTrue();
        r.Status.Should().Be(ReturnStatus.Withdrawn);
        r.WithdrawnAtUtc.Should().Be(Delivered.AddDays(2));
        r.Withdraw("MW-261101-ABC123", Delivered.AddDays(3)).Should().BeFalse("again changes nothing");
        r.WithdrawnAtUtc.Should().Be(Delivered.AddDays(2));
        r.DomainEvents.OfType<ReturnWithdrawnEvent>().Should().ContainSingle();

        var handled = Open(Delivered.AddDays(1));
        typeof(ReturnRequest).GetProperty(nameof(ReturnRequest.Status))!.SetValue(handled, ReturnStatus.Approved);
        FluentActions.Invoking(() => handled.Withdraw("MW-261101-ABC123", Delivered.AddDays(2)))
            .Should().Throw<ConflictException>().Which.Code.Should().Be("RETURN_NOT_WITHDRAWABLE");
    }

    [Fact]
    public void Options_DefaultToSevenDays_AndRefuseAWindowOutsideOneToSixty()
    {
        ReturnOptions.Validated(null).WindowDays.Should().Be(7);
        ReturnOptions.Validated(null).NonReturnableSlugs().Should().BeEmpty();
        new ReturnOptions { NonReturnableCategorySlugs = " Cables , relays,," }.NonReturnableSlugs().Should().BeEquivalentTo(["cables", "relays"]);
        FluentActions.Invoking(() => ReturnOptions.Validated(new ReturnOptions { WindowDays = 0 })).Should().Throw<InvalidOperationException>();
        FluentActions.Invoking(() => ReturnOptions.Validated(new ReturnOptions { WindowDays = 61 })).Should().Throw<InvalidOperationException>();
    }
}
