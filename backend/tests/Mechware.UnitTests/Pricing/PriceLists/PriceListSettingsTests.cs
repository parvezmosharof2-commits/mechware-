using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Pricing.PriceLists;
using Mechware.Infrastructure.Pricing;

namespace Mechware.UnitTests.Pricing.PriceLists;

/// <summary>D2.4: price-list permissions per role (owner decision 3), <c>Pricing:AllowBelowCost</c> and stored margin floor rows.</summary>
public class PriceListSettingsTests
{
    private static readonly string[] PricingPermissions =
    [
        Permissions.PriceListsView, Permissions.PriceListsEdit, Permissions.PriceListsApprove,
        Permissions.PriceListsApproveBelowFloor, Permissions.PricingViewCost, Permissions.PricingManageFloors,
    ];

    [Fact]
    public void PermissionCodes_AreTheOnesInTheAdr() =>
        PricingPermissions.Should().Equal("pricelists.view", "pricelists.edit", "pricelists.approve",
            "pricelists.approve_below_floor", "pricing.view_cost", "pricing.manage_floors");

    // Inventory of the pricing permissions every role holds by default; a change here is a deliberate RBAC decision.
    [Theory]
    [InlineData(Permissions.Roles.Buyer)]
    [InlineData(Permissions.Roles.Seller)]
    [InlineData(Permissions.Roles.Moderator)]
    [InlineData(Permissions.Roles.Support, Permissions.PriceListsView, Permissions.PriceListsEdit)]
    [InlineData(Permissions.Roles.Finance, Permissions.PriceListsView, Permissions.PriceListsApprove, Permissions.PricingViewCost)]
    [InlineData(Permissions.Roles.Admin, Permissions.PriceListsView, Permissions.PriceListsEdit, Permissions.PriceListsApprove,
        Permissions.PriceListsApproveBelowFloor, Permissions.PricingViewCost, Permissions.PricingManageFloors)]
    public void EachRole_HoldsExactlyItsPricingPermissions(string role, params string[] expected) =>
        Permissions.GetDefaultPermissionsForRole(role).Intersect(PricingPermissions).Should().BeEquivalentTo(expected);

    [Fact]
    public void NoRoleThatEditsPriceLists_MayApproveBelowFloorOrManageFloors_ExceptAdmin()
    {
        foreach (var role in new[] { Permissions.Roles.Support, Permissions.Roles.Finance })
            Permissions.GetDefaultPermissionsForRole(role).Should().NotContain([Permissions.PriceListsApproveBelowFloor, Permissions.PricingManageFloors]);
    }

    private static IConfiguration Config(string? value) => new ConfigurationBuilder()
        .AddInMemoryCollection(value is null ? [] : [new KeyValuePair<string, string?>("Pricing:AllowBelowCost", value)]).Build();

    [Theory]
    [InlineData(null, true, false)]
    [InlineData(null, false, false)]
    [InlineData("false", false, false)]
    [InlineData("true", true, true)]
    public void AllowBelowCost_IsOffUnlessTurnedOnInDevelopmentOrTesting(string? configured, bool devOrTesting, bool expected) =>
        PricingOptions.From(Config(configured), devOrTesting).AllowBelowCost.Should().Be(expected);

    [Theory]
    [InlineData("true", "only allowed in Development or Testing")]
    [InlineData("yes", "must be true or false")]
    public void OutsideDevelopment_AllowBelowCostStopsTheHost(string configured, string message) =>
        FluentActions.Invoking(() => PricingOptions.From(Config(configured), developmentOrTesting: false))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{message}*");

    [Fact]
    public void DefaultOptions_RefuseBelowCost() => new PricingOptions().AllowBelowCost.Should().BeFalse();

    [Theory]
    [InlineData(MarginFloorScope.Global, true, false, 10)]
    [InlineData(MarginFloorScope.Brand, false, false, 10)]
    [InlineData(MarginFloorScope.BrandAndCategory, true, false, 10)]
    [InlineData(MarginFloorScope.Global, false, false, 10.005)]
    [InlineData(MarginFloorScope.Global, false, false, 1000.01)]
    public void MarginFloor_WithAWrongShapeOrMarkup_IsRefused(MarginFloorScope scope, bool brand, bool category, double markup) =>
        FluentActions.Invoking(() => MarginFloor.Create(Guid.NewGuid(), scope, brand ? Guid.NewGuid() : null, category ? Guid.NewGuid() : null,
            (decimal)markup, Guid.NewGuid(), DateTime.UtcNow)).Should().Throw<ValidationException>();

    [Fact]
    public void MarginFloor_ToRow_IsWhatThePolicyReads()
    {
        var brand = Guid.NewGuid();
        MarginFloor.Create(Guid.NewGuid(), MarginFloorScope.Brand, brand, null, 12.5m, Guid.NewGuid(), DateTime.UtcNow).ToRow()
            .Should().Be(new MarginFloorRow(MarginFloorScope.Brand, brand, null, 12.5m));
    }
}
