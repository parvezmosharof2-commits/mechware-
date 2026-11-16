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
        CreditPermissions.Should().Equal("credit.record_payment", "credit.verify_payment");
}
