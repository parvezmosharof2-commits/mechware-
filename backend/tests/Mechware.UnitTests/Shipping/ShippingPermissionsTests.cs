using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Domain.Shipping;

namespace Mechware.UnitTests.Shipping;

/// <summary>E8.4a: shipping permissions per role (a change here is a deliberate RBAC decision) and the zone's edit methods.</summary>
public class ShippingPermissionsTests
{
    private static readonly string[] ShippingPermissions = [Permissions.ShippingView, Permissions.ShippingManage];

    [Fact]
    public void PermissionCodes_AreStable() => ShippingPermissions.Should().Equal("shipping.view", "shipping.manage");

    [Theory]
    [InlineData(Permissions.Roles.Buyer)]
    [InlineData(Permissions.Roles.Seller)]
    [InlineData(Permissions.Roles.Moderator)]
    [InlineData(Permissions.Roles.Support, Permissions.ShippingView)]
    [InlineData(Permissions.Roles.Finance, Permissions.ShippingView)]
    [InlineData(Permissions.Roles.Admin, Permissions.ShippingView, Permissions.ShippingManage)]
    public void EachRole_HoldsExactlyItsShippingPermissions(string role, params string[] expected) =>
        Permissions.GetDefaultPermissionsForRole(role).Intersect(ShippingPermissions).Should().BeEquivalentTo(expected);

    private static ShippingRateCard Card(long fee = 100) => new([new WeightBand(1_000, fee), new WeightBand(5_000, fee * 2)], null, null, null, 10_000);

    [Fact]
    public void SetRates_KeepsTheKeyOfABandWhoseWeightStays_AndReplacesTheRest()
    {
        var zone = new ShippingZone(Guid.NewGuid(), "z", "Z", "জ", [Guid.NewGuid()], Card());
        zone.SetRates(new ShippingRateCard([new WeightBand(1_000, 150), new WeightBand(9_000, 900)], 5_000, null, null, 10_000));

        zone.Bands.Select(b => (b.MaxGrams, b.FeeMinor)).Should().BeEquivalentTo([(1_000, 150L), (9_000, 900L)]);
        zone.ToConfig().Rates.FreeShippingThresholdMinor.Should().Be(5_000);
    }

    [Fact]
    public void SetDistricts_KeepsOnesThatStay_AddsNewOnes_AndRefusesADuplicate()
    {
        var (a, b, c) = (Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid());
        var zone = new ShippingZone(Guid.NewGuid(), "z", "Z", "জ", [a, b], Card());
        zone.SetDistricts([b, c]);
        zone.Districts.Select(d => d.DistrictId).Should().BeEquivalentTo([b, c]);

        zone.Invoking(z => z.SetDistricts([a, a])).Should().Throw<ValidationException>();
    }

    [Fact]
    public void Edit_ChangesNamesAndSwitch_ButNotTheCode_AndNeedsBothNames()
    {
        var zone = new ShippingZone(Guid.NewGuid(), "dhaka", "Z", "জ", [Guid.NewGuid()], Card());
        zone.Edit(" New ", "নতুন", isActive: false);
        (zone.Code, zone.NameEn, zone.IsActive).Should().Be(("dhaka", "New", false));
        zone.Invoking(z => z.Edit("x", " ", true)).Should().Throw<ValidationException>();
    }
}
