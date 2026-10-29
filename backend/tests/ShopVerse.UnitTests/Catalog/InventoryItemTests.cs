using FluentAssertions;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Inventory;

namespace ShopVerse.UnitTests.Catalog;

public class InventoryItemTests
{
    private static InventoryItem Item(int onHand, int threshold = 5) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), onHand, threshold);

    [Fact]
    public void Available_IsOnHandMinusReserved()
    {
        var i = Item(10);
        i.TryReserve(4).Should().BeTrue();
        (i.OnHand, i.Reserved, i.Available).Should().Be((10, 4, 6));
    }

    [Fact]
    public void Reserve_MoreThanAvailable_FailsWithoutChangingState()
    {
        var i = Item(3);
        i.TryReserve(4).Should().BeFalse();
        (i.OnHand, i.Reserved).Should().Be((3, 0));
    }

    [Fact]
    public void Commit_DeductsOnHandAndReserved()
    {
        var i = Item(10);
        i.TryReserve(4);
        i.Commit(4);
        (i.OnHand, i.Reserved, i.Available).Should().Be((6, 0, 6));
    }

    [Fact]
    public void Release_ReturnsStockToAvailable_AndCannotOverRelease()
    {
        var i = Item(10);
        i.TryReserve(4);
        i.Release(4);
        i.Available.Should().Be(10);

        var act = () => i.Release(1);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void StockLow_FiresOnceWhenCrossingThreshold_NotOnEveryDecrement()
    {
        var i = Item(10, threshold: 5);
        i.TryReserve(2);                     // 8 available — above threshold
        i.DomainEvents.Should().BeEmpty();

        i.TryReserve(3);                     // 5 available — crosses (≤ 5)
        i.DomainEvents.OfType<StockLowDomainEvent>().Should().ContainSingle(e => e.Available == 5 && e.Threshold == 5);

        i.TryReserve(1);                     // 4 — already low, no new event
        i.DomainEvents.OfType<StockLowDomainEvent>().Should().HaveCount(1);
    }

    [Fact]
    public void StockLow_RearmsAfterRestock()
    {
        var i = Item(6, threshold: 5);
        i.TryReserve(2);
        i.ClearDomainEvents();

        i.AdjustOnHand(50);                  // restock
        i.TryReserve(46);                    // back down to 2
        i.DomainEvents.OfType<StockLowDomainEvent>().Should().ContainSingle();
    }

    [Fact]
    public void AdjustOnHand_CannotGoBelowReserved()
    {
        var i = Item(10);
        i.TryReserve(6);
        var act = () => i.AdjustOnHand(5);
        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void Reservation_ExpiresFifteenMinutesAfterCreation_ByDefault()
    {
        var now = new DateTime(2026, 11, 1, 10, 0, 0, DateTimeKind.Utc);
        var r = new StockReservation(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 1, 100, now);

        r.ExpiresAtUtc.Should().Be(now.AddMinutes(15));
        r.IsExpired(now.AddMinutes(14)).Should().BeFalse();
        r.IsExpired(now.AddMinutes(15)).Should().BeTrue();

        r.MarkFulfilled();
        r.IsExpired(now.AddHours(1)).Should().BeFalse("only active holds expire");
        var again = () => r.MarkCancelled();
        again.Should().Throw<ValidationException>();
    }
}
