using System.Reflection;
using FluentAssertions;
using Mechware.Api.Authorization;
using Mechware.Api.Controllers;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Returns;

namespace Mechware.UnitTests.Orders;

/// <summary>F2.3a staff moves on a return: the status graph, every refusal, the rejection reason, the inspection limits, events, permission.</summary>
public class ReturnDecisionTests
{
    private static readonly Guid Line1 = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Line2 = Guid.Parse("00000000-0000-0000-0000-000000000002");
    private static readonly Guid Staff = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly DateTime Delivered = new(2026, 11, 1, 6, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = Delivered.AddDays(2);
    private const string Number = "MW-261101-ABC123";

    /// <summary>A return of 3 of line 1 and 1 of line 2, moved to <paramref name="status"/> along the allowed path.</summary>
    private static ReturnRequest In(ReturnStatus status)
    {
        var r = ReturnRequest.Open(Guid.NewGuid(), Number, OrderStatus.Delivered, Delivered, null, Guid.NewGuid(), ReturnReason.Damaged, null,
            [new ReturnLineRequest(Line1, 3), new ReturnLineRequest(Line2, 1)],
            [new ReturnableLine(Line1, 3, 0, true), new ReturnableLine(Line2, 1, 0, true)], 7, Now, "key-12345678", new string('a', 64));
        switch (status)
        {
            case ReturnStatus.Withdrawn: r.Withdraw(Number, Now); break;
            case ReturnStatus.Rejected: r.Reject(Staff, "Outside the policy", Number, Now); break;
            case ReturnStatus.Approved: r.Approve(Staff, Number, Now); break;
            case ReturnStatus.Received:
                r.Approve(Staff, Number, Now);
                r.MarkReceived(Staff, Number, Now);
                break;
            case ReturnStatus.Inspected:
                r.Approve(Staff, Number, Now);
                r.MarkReceived(Staff, Number, Now);
                r.Inspect(Staff, Inspection(r, 3, 3, 1, 1), Number, Now);
                break;
        }
        r.Status.Should().Be(status);
        r.ClearDomainEvents();
        return r;
    }

    private static IReadOnlyList<ReturnInspection> Inspection(ReturnRequest r, int accepted1, int restock1, int accepted2, int restock2) =>
        ReturnRequest.NormaliseInspection([
            new(r.Lines.Single(l => l.OrderItemId == Line1).Id, accepted1, restock1, null),
            new(r.Lines.Single(l => l.OrderItemId == Line2).Id, accepted2, restock2, null)]);

    [Fact]
    public void TheAllowedPath_SetsWhoAndWhen_AndRaisesOneEventPerStep()
    {
        var r = In(ReturnStatus.Requested);
        r.Approve(Staff, Number, Now.AddMinutes(1));
        (r.Status, r.DecidedBy, r.DecidedAtUtc).Should().Be((ReturnStatus.Approved, Staff, Now.AddMinutes(1)));
        r.MarkReceived(Staff, Number, Now.AddMinutes(2));
        (r.Status, r.ReceivedBy, r.ReceivedAtUtc).Should().Be((ReturnStatus.Received, Staff, Now.AddMinutes(2)));
        r.Inspect(Staff, Inspection(r, 2, 1, 1, 0), Number, Now.AddMinutes(3));
        (r.Status, r.InspectedBy, r.InspectedAtUtc).Should().Be((ReturnStatus.Inspected, Staff, Now.AddMinutes(3)));
        r.Lines.Select(l => (l.OrderItemId, l.AcceptedQuantity, l.RestockQuantity)).Should().BeEquivalentTo(new[] { (Line1, (int?)2, (int?)1), (Line2, (int?)1, (int?)0) });
        r.DomainEvents.Select(e => e.GetType()).Should().Equal(typeof(ReturnApprovedEvent), typeof(ReturnReceivedEvent), typeof(ReturnInspectedEvent));
        r.DomainEvents.OfType<ReturnInspectedEvent>().Single().Should().Match<ReturnInspectedEvent>(e => e.AcceptedQuantity == 3 && e.RestockQuantity == 1);
    }

    [Fact]
    public void Reject_KeepsTheReason_AndRaisesAnEvent()
    {
        var r = In(ReturnStatus.Requested);
        r.Reject(Staff, ReturnRequest.NormaliseRejectionReason("  Installed and used​ "), Number, Now);
        (r.Status, r.RejectionReason, r.DecidedBy).Should().Be((ReturnStatus.Rejected, "Installed and used", Staff));
        r.DomainEvents.Should().ContainSingle().Which.Should().BeOfType<ReturnRejectedEvent>();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("    ")]
    [InlineData("abcd")]
    public void ARejectionReason_HasFiveTo500Characters(string? reason)
    {
        FluentActions.Invoking(() => ReturnRequest.NormaliseRejectionReason(reason)).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseRejectionReason(new string('x', 501))).Should().Throw<ValidationException>();
        ReturnRequest.NormaliseRejectionReason(new string('x', 500)).Should().HaveLength(500);
        ReturnRequest.NormaliseRejectionReason("abcde").Should().Be("abcde");
    }

    public static TheoryData<ReturnStatus, string> Refused() => new()
    {
        { ReturnStatus.Approved, "approve" }, { ReturnStatus.Rejected, "approve" }, { ReturnStatus.Withdrawn, "approve" },
        { ReturnStatus.Received, "approve" }, { ReturnStatus.Inspected, "approve" },
        { ReturnStatus.Approved, "reject" }, { ReturnStatus.Rejected, "reject" }, { ReturnStatus.Withdrawn, "reject" },
        { ReturnStatus.Received, "reject" }, { ReturnStatus.Inspected, "reject" },
        { ReturnStatus.Requested, "receive" }, { ReturnStatus.Rejected, "receive" }, { ReturnStatus.Withdrawn, "receive" },
        { ReturnStatus.Received, "receive" }, { ReturnStatus.Inspected, "receive" },
        { ReturnStatus.Requested, "inspect" }, { ReturnStatus.Approved, "inspect" }, { ReturnStatus.Rejected, "inspect" },
        { ReturnStatus.Withdrawn, "inspect" }, { ReturnStatus.Inspected, "inspect" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public void AMoveFromTheWrongStatus_Is409_AndChangesNothing(ReturnStatus status, string move)
    {
        var r = In(status);
        Action act = move switch
        {
            "approve" => () => r.Approve(Staff, Number, Now),
            "reject" => () => r.Reject(Staff, "Outside the policy", Number, Now),
            "receive" => () => r.MarkReceived(Staff, Number, Now),
            _ => () => r.Inspect(Staff, Inspection(r, 1, 1, 1, 1), Number, Now)
        };
        act.Should().Throw<ConflictException>().Which.Code.Should().Be("RETURN_INVALID_TRANSITION");
        r.Status.Should().Be(status);
        r.DomainEvents.Should().BeEmpty();
    }

    [Theory]
    [InlineData(ReturnStatus.Approved)]
    [InlineData(ReturnStatus.Received)]
    [InlineData(ReturnStatus.Inspected)]
    [InlineData(ReturnStatus.Rejected)]
    public void OnceStaffDecided_TheBuyerCannotWithdraw(ReturnStatus status) =>
        FluentActions.Invoking(() => In(status).Withdraw(Number, Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("RETURN_NOT_WITHDRAWABLE");

    [Fact]
    public void Inspect_AcceptsAtMostWhatWasAsked_AndNamesEveryLineOnce()
    {
        var r = In(ReturnStatus.Received);
        FluentActions.Invoking(() => r.Inspect(Staff, Inspection(r, 4, 0, 1, 0), Number, Now))
            .Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("lines[0].acceptedQty");
        FluentActions.Invoking(() => r.Inspect(Staff, [new(r.Lines[0].Id, 1, 0, null)], Number, Now))
            .Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("lines");
        FluentActions.Invoking(() => r.Inspect(Staff, [new(r.Lines[0].Id, 1, 0, null), new(Guid.NewGuid(), 1, 0, null)], Number, Now))
            .Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be("lines");
        (r.Status, r.Lines.All(l => l.AcceptedQuantity is null)).Should().Be((ReturnStatus.Received, true));

        r.Inspect(Staff, Inspection(r, 0, 0, 1, 1), Number, Now);   // nothing accepted of line 1 is a valid outcome
        r.Lines.Single(l => l.OrderItemId == Line1).AcceptedQuantity.Should().Be(0);
    }

    [Theory]
    [InlineData(-1, 0, "lines[0].acceptedQty")]
    [InlineData(10_001, 0, "lines[0].acceptedQty")]
    [InlineData(1, 2, "lines[0].restockQty")]
    [InlineData(1, -1, "lines[0].restockQty")]
    public void TheInspectionShape_IsChecked_BeforeAnythingIsLookedUp(int accepted, int restock, string field) =>
        FluentActions.Invoking(() => ReturnRequest.NormaliseInspection([new(Guid.NewGuid(), accepted, restock, null)]))
            .Should().Throw<ValidationException>().Which.Errors.Single().Field.Should().Be(field);

    [Fact]
    public void TheInspectionNote_IsPlainTrimmedText_AtMost500_AndLinesAreNamedOnce()
    {
        var id = Guid.NewGuid();
        ReturnRequest.NormaliseInspection([new(id, 1, 1, "  scratched housing ")]).Single().Note.Should().Be("scratched housing");
        ReturnRequest.NormaliseInspection([new(id, 1, 1, "   ")]).Single().Note.Should().BeNull();
        FluentActions.Invoking(() => ReturnRequest.NormaliseInspection([new(id, 1, 1, new string('n', 501))])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseInspection([new(id, 1, 1, null), new(id, 1, 0, null)])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseInspection([new(Guid.Empty, 1, 1, null)])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseInspection([])).Should().Throw<ValidationException>();
        FluentActions.Invoking(() => ReturnRequest.NormaliseInspection(null)).Should().Throw<ValidationException>();
    }

    [Theory]
    [InlineData(Permissions.Roles.Support, true)]
    [InlineData(Permissions.Roles.Admin, true)]
    [InlineData(Permissions.Roles.Finance, false)]
    [InlineData(Permissions.Roles.Moderator, false)]
    [InlineData(Permissions.Roles.Seller, false)]
    [InlineData(Permissions.Roles.Buyer, false)]
    public void ReturnsManage_IsSupportAndAdmin(string role, bool granted) =>
        Permissions.GetDefaultPermissionsForRole(role).Contains(Permissions.ReturnsManage).Should().Be(granted);

    [Theory]
    [InlineData(nameof(AdminReturnsController.Approve))]
    [InlineData(nameof(AdminReturnsController.Reject))]
    [InlineData(nameof(AdminReturnsController.Received))]
    [InlineData(nameof(AdminReturnsController.Inspect))]
    public void EveryStaffMove_NeedsReturnsManage(string action) =>
        typeof(AdminReturnsController).GetMethod(action)!.GetCustomAttribute<RequirePermissionAttribute>()!.Permission.Should().Be(Permissions.ReturnsManage);
}
