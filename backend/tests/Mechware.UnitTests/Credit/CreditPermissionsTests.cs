using FluentAssertions;
using Mechware.Domain.Identity;

namespace Mechware.UnitTests.Credit;

/// <summary>ADR-0010 §5 (threat model CS3): Finance and Admin record and verify payments; no other role holds either permission.</summary>
public class CreditPermissionsTests
{
    private static readonly string[] CreditPermissions = [Permissions.CreditRecordPayment, Permissions.CreditVerifyPayment];

    [Theory]
    [InlineData(Permissions.Roles.Finance, true)]
    [InlineData(Permissions.Roles.Admin, true)]
    [InlineData(Permissions.Roles.Support, false)]
    [InlineData(Permissions.Roles.Seller, false)]
    [InlineData(Permissions.Roles.Moderator, false)]
    [InlineData(Permissions.Roles.Buyer, false)]
    public void OnlyFinanceAndAdmin_RecordAndVerifyPayments(string role, bool granted) =>
        Permissions.GetDefaultPermissionsForRole(role).Intersect(CreditPermissions).Should().BeEquivalentTo(granted ? CreditPermissions : []);

    [Fact]
    public void ThePermissionNames_AreTheOnesTheDesignNames() =>
        CreditPermissions.Concat([Permissions.CreditAdjust, Permissions.CreditAdjustApprove]).Should()
            .Equal("credit.record_payment", "credit.verify_payment", "credit.adjust", "credit.adjust_approve");

    /// <summary>§5 (CS20): Finance and Admin ask for adjustments; only an Admin approves one (someone else's, with step-up).</summary>
    [Theory]
    [InlineData(Permissions.Roles.Finance, true, false)]
    [InlineData(Permissions.Roles.Admin, true, true)]
    [InlineData(Permissions.Roles.Support, false, false)]
    [InlineData(Permissions.Roles.Seller, false, false)]
    [InlineData(Permissions.Roles.Moderator, false, false)]
    [InlineData(Permissions.Roles.Buyer, false, false)]
    public void OnlyFinanceAndAdmin_RequestAdjustments_AndOnlyAdmin_ApprovesThem(string role, bool request, bool approve)
    {
        var granted = Permissions.GetDefaultPermissionsForRole(role);
        (granted.Contains(Permissions.CreditAdjust), granted.Contains(Permissions.CreditAdjustApprove)).Should().Be((request, approve));
    }
}
