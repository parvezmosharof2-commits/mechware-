using System.Reflection;
using FluentAssertions;
using Mechware.Api.Authorization;
using Mechware.Api.Controllers;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Returns;

namespace Mechware.UnitTests.Orders;

/// <summary>F2.3b: the money of an inspected return, its four eyes, and who may ask or approve it.</summary>
public class ReturnMoneyBackTests
{
    private static readonly Guid Line = Guid.Parse("00000000-0000-0000-0000-000000000001");
    private static readonly Guid Asker = Guid.Parse("00000000-0000-0000-0000-0000000000aa");
    private static readonly Guid Approver = Guid.Parse("00000000-0000-0000-0000-0000000000bb");
    private static readonly DateTime Now = new(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc);
    private const string Number = "MW-261101-ABC123";

    private static ReturnRequest Inspected(int accepted = 2)
    {
        var r = ReturnRequest.Open(Guid.NewGuid(), Number, OrderStatus.Delivered, Now.AddDays(-2), null, Guid.NewGuid(), ReturnReason.Damaged, null,
            [new ReturnLineRequest(Line, 3)], [new ReturnableLine(Line, 3, 0, true)], 7, Now, "key-12345678", new string('a', 64));
        r.Approve(Asker, Number, Now);
        r.MarkReceived(Asker, Number, Now);
        r.Inspect(Asker, ReturnRequest.NormaliseInspection([new(r.Lines[0].Id, accepted, 0, null)]), Number, Now);
        r.ClearDomainEvents();
        return r;
    }

    [Fact]
    public void ACreditNote_IsAskedOnce_AndOnlyAnotherPerson_MovesTheReturnToCreditNoted()
    {
        var r = Inspected();
        r.RequestMoneyBack(ReturnSettlement.CreditNote, null, null, Asker, Number, Now);
        (r.Status, r.Settlement, r.SettlementRequestedBy).Should().Be((ReturnStatus.Inspected, ReturnSettlement.CreditNote, Asker));
        r.DomainEvents.OfType<ReturnMoneyBackRequestedEvent>().Should().ContainSingle();
        Assert.Throws<ConflictException>(() => r.RequestMoneyBack(ReturnSettlement.CreditNote, null, null, Asker, Number, Now)).Code
            .Should().Be("RETURN_INVALID_TRANSITION");
        Assert.Throws<ConflictException>(() => r.MarkRefunded(Approver, Now)).Code.Should().Be("RETURN_INVALID_TRANSITION", "a credit note never refunds");
        Assert.Throws<ConflictException>(() => r.MarkCreditNoted(Asker, Now)).Code.Should().Be(ReturnRequest.FourEyes);

        r.MarkCreditNoted(Approver, Now.AddMinutes(5));
        (r.Status, r.SettledBy, r.SettledAtUtc).Should().Be((ReturnStatus.CreditNoted, Approver, Now.AddMinutes(5)));
        Assert.Throws<ConflictException>(() => r.MarkCreditNoted(Approver, Now)).Code.Should().Be("RETURN_INVALID_TRANSITION");
    }

    [Fact]
    public void ARefundOrReduction_CarriesItsPositiveAmount_ARefundItsRefund_AndNothingIsAskedOfAReturnWithoutAcceptedUnits()
    {
        Assert.Throws<ConflictException>(() => Inspected(accepted: 0).RequestMoneyBack(ReturnSettlement.CreditReduction, 100, null, Asker, Number, Now))
            .Code.Should().Be(ReturnRequest.NothingToSettle);
        Assert.Throws<InvalidOperationException>(() => Inspected().RequestMoneyBack(ReturnSettlement.Refund, 100, null, Asker, Number, Now));
        Assert.Throws<InvalidOperationException>(() => Inspected().RequestMoneyBack(ReturnSettlement.CreditReduction, 0, null, Asker, Number, Now));
        Assert.Throws<InvalidOperationException>(() => Inspected().RequestMoneyBack(ReturnSettlement.CreditNote, 100, null, Asker, Number, Now));

        var r = Inspected();
        r.RequestMoneyBack(ReturnSettlement.CreditReduction, 1_500, null, Asker, Number, Now);
        Assert.Throws<ConflictException>(() => r.MarkRefunded(Asker, Now)).Code.Should().Be(ReturnRequest.FourEyes);
        r.MarkRefunded(Approver, Now);
        (r.Status, r.SettlementAmountMinor, r.SourceKey).Should().Be((ReturnStatus.Refunded, 1_500L, $"return:{r.Id:N}"));
    }

    [Fact]
    public void AskingNeedsReturnsManage_ApprovingACreditReduction_PaymentsRefundApproveAndAStepUpCode()
    {
        var ask = typeof(AdminReturnsController).GetMethod(nameof(AdminReturnsController.MoneyBack))!;
        ask.GetCustomAttribute<RequirePermissionAttribute>()!.Permission.Should().Be(Permissions.ReturnsManage);
        ask.GetCustomAttributes<RequireStepUpAttribute>().Should().BeEmpty("asking moves no money");
        var approve = typeof(AdminReturnsController).GetMethod(nameof(AdminReturnsController.ApproveMoneyBack))!;
        approve.GetCustomAttribute<RequirePermissionAttribute>()!.Permission.Should().Be(Permissions.PaymentsRefundApprove);
        approve.GetCustomAttributes<RequireStepUpAttribute>().Select(a => a.Action).Should().Equal("refund.approve");
    }
}
