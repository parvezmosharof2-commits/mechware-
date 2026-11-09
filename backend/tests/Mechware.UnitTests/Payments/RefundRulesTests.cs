using System.Reflection;
using FluentAssertions;
using Mechware.Api.Authorization;
using Mechware.Api.Controllers;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Payments;

namespace Mechware.UnitTests.Payments;

/// <summary>Threat model v2, P3: who may ask for a refund, who may approve one, and the refund's own state rules.</summary>
public class RefundRulesTests
{
    private static readonly DateTime Now = new(2026, 11, 5, 10, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(Permissions.Roles.Support, true, false)]
    [InlineData(Permissions.Roles.Finance, true, true)]
    [InlineData(Permissions.Roles.Admin, true, true)]
    [InlineData(Permissions.Roles.Buyer, false, false)]
    [InlineData(Permissions.Roles.Seller, false, false)]
    [InlineData(Permissions.Roles.Moderator, false, false)]
    public void OnlyFinanceAndAdmin_MayApprove_SupportMayOnlyAsk(string role, bool mayAsk, bool mayApprove)
    {
        var granted = Permissions.GetDefaultPermissionsForRole(role);
        granted.Contains(Permissions.OrdersRefund).Should().Be(mayAsk);
        granted.Contains(Permissions.PaymentsRefundApprove).Should().Be(mayApprove);
    }

    [Theory]
    [InlineData(nameof(AdminRefundsController.Queue), Permissions.OrdersRefund)]
    [InlineData(nameof(AdminRefundsController.Request), Permissions.OrdersRefund)]
    [InlineData(nameof(AdminRefundsController.Approve), Permissions.PaymentsRefundApprove)]
    [InlineData(nameof(AdminRefundsController.Reject), Permissions.PaymentsRefundApprove)]
    public void EachRefundEndpoint_RequiresItsPermission(string action, string permission)
    {
        var method = typeof(AdminRefundsController).GetMethod(action, BindingFlags.Public | BindingFlags.Instance)!;
        method.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Permission).Should().Equal(permission);
    }

    [Fact]
    public void RejectingACompany_NeedsCompaniesManage()
    {
        var method = typeof(AdminCompaniesController).GetMethod(nameof(AdminCompaniesController.Reject), BindingFlags.Public | BindingFlags.Instance)!;
        method.GetCustomAttributes<RequirePermissionAttribute>().Select(a => a.Permission).Should().Equal(Permissions.CompaniesManage);
    }

    private static Refund NewRefund(Guid asker) => new(Guid.NewGuid(), Guid.NewGuid(), 1_000, "BDT", "Order cancelled", asker, Now);

    [Fact]
    public void TheAsker_CanNeverApprove_EvenAfterAFailure()
    {
        var asker = Guid.NewGuid();
        var refund = NewRefund(asker);
        refund.Invoking(r => r.Approve(asker, Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("REFUND_FOUR_EYES");

        refund.Approve(Guid.NewGuid(), Now);
        refund.Fail("timeout", Now);
        refund.Invoking(r => r.Approve(asker, Now)).Should().Throw<ConflictException>().Which.Code.Should().Be("REFUND_FOUR_EYES");
    }

    [Fact]
    public void FinishedRefunds_CannotBeApprovedOrRejectedAgain()
    {
        var approver = Guid.NewGuid();
        var done = NewRefund(Guid.NewGuid());
        done.Approve(approver, Now);
        done.Complete("rf_1", Now);
        done.Invoking(r => r.Approve(approver, Now.AddHours(1))).Should().Throw<ConflictException>();
        done.Invoking(r => r.Reject(approver, "too late now", Now)).Should().Throw<ConflictException>();

        var rejected = NewRefund(Guid.NewGuid());
        rejected.Reject(approver, "not justified", Now);
        rejected.Invoking(r => r.Approve(approver, Now)).Should().Throw<ConflictException>();
        rejected.HoldsAmount.Should().BeFalse();
    }

    [Fact]
    public void AProcessingRefund_IsReapprovableOnlyAfterItIsStuck()
    {
        var approver = Guid.NewGuid();
        var refund = NewRefund(Guid.NewGuid());
        refund.Approve(approver, Now);
        refund.Invoking(r => r.Approve(approver, Now + Refund.StuckAfter - TimeSpan.FromSeconds(1))).Should().Throw<ConflictException>();
        refund.Invoking(r => r.Approve(approver, Now + Refund.StuckAfter)).Should().NotThrow();
    }

    [Theory]
    [InlineData(0, "Order cancelled")]
    [InlineData(-5, "Order cancelled")]
    [InlineData(100, "ok")]
    [InlineData(100, "    ")]
    public void ARefundNeedsAPositiveAmount_AndARealReason(long amount, string reason) =>
        FluentActions.Invoking(() => new Refund(Guid.NewGuid(), Guid.NewGuid(), amount, "BDT", reason, Guid.NewGuid(), Now))
            .Should().Throw<ValidationException>();
}
