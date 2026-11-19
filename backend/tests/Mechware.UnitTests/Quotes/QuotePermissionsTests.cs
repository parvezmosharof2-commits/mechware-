using FluentAssertions;
using Mechware.Domain.Identity;

namespace Mechware.UnitTests.Quotes;

/// <summary>D4.4: quote permissions per role (ADR-0011 §13). A change here is a deliberate RBAC decision.</summary>
public class QuotePermissionsTests
{
    private static readonly string[] QuotePermissions =
        [Permissions.QuotesView, Permissions.QuotesEdit, Permissions.QuotesApprove, Permissions.QuotesApproveBelowFloor];

    [Fact]
    public void PermissionCodes_AreTheOnesInTheAdr() =>
        QuotePermissions.Should().Equal("quotes.view", "quotes.edit", "quotes.approve", "quotes.approve_below_floor");

    [Theory]
    [InlineData(Permissions.Roles.Buyer)]
    [InlineData(Permissions.Roles.Seller)]
    [InlineData(Permissions.Roles.Moderator)]
    [InlineData(Permissions.Roles.Support, Permissions.QuotesView, Permissions.QuotesEdit)]
    [InlineData(Permissions.Roles.Finance, Permissions.QuotesView, Permissions.QuotesApprove)]
    [InlineData(Permissions.Roles.Admin, Permissions.QuotesView, Permissions.QuotesEdit, Permissions.QuotesApprove, Permissions.QuotesApproveBelowFloor)]
    public void EachRole_HoldsExactlyItsQuotePermissions(string role, params string[] expected) =>
        Permissions.GetDefaultPermissionsForRole(role).Intersect(QuotePermissions).Should().BeEquivalentTo(expected);

    [Fact]
    public void Support_SeesTheFlagsButNotTheCostFigures() =>
        Permissions.GetDefaultPermissionsForRole(Permissions.Roles.Support).Should().NotContain(Permissions.PricingViewCost);
}
