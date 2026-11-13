using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Mechware.Application.Shipping;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Orders;
using Mechware.Domain.Shipping;
using Mechware.Infrastructure.Shipping;

namespace Mechware.UnitTests.Shipping;

/// <summary>E8.2: the seam between orders and zones, and the PO number rule. No database: the zones come from a stub.</summary>
public class OrderShippingQuoterTests
{
    private static readonly Guid District = Guid.Parse("d0000000-0000-0000-0000-000000000001");
    private static readonly Guid SellerA = Guid.Parse("a0000000-0000-0000-0000-00000000000a");
    private static readonly Guid SellerB = Guid.Parse("b0000000-0000-0000-0000-00000000000b");

    private sealed class Zones(params ShippingZoneConfig[] zones) : IShippingRateProvider
    {
        public int Calls { get; private set; }
        public Task<IReadOnlyList<ShippingZoneConfig>> GetActiveZonesAsync(CancellationToken ct) { Calls++; return Task.FromResult<IReadOnlyList<ShippingZoneConfig>>(zones); }
    }

    // Test data only, not real tariffs: up to 1 kg 60.00, up to 5 kg 120.00.
    private static ShippingZoneConfig Zone(long? freeFrom = null, int? heavy = null, int? oversizeMm = null) =>
        new(Guid.NewGuid(), "test", new HashSet<Guid> { District },
            new ShippingRateCard([new WeightBand(1_000, 6_000), new WeightBand(5_000, 12_000)], freeFrom, heavy, oversizeMm, 50_000));

    private static ShippingQuoteLine Line(Guid seller, int qty, decimal kg = 0.5m, string sku = "SKU-1", decimal? sideCm = 10m) =>
        new(Guid.NewGuid(), sku, seller, qty, kg, sideCm, sideCm, sideCm);

    private static Task<OrderShipping> Quote(IShippingRateProvider zones, bool allowFree, Guid? district, long goods, params ShippingQuoteLine[] lines) =>
        new OrderShippingQuoter(zones, new ShippingOptions { AllowFreeWhenNoZones = allowFree }).QuoteAsync(district, lines, goods, CancellationToken.None);

    private static async Task<UnprocessableException> Refused(Func<Task> act, string code)
    {
        var e = (await act.Should().ThrowAsync<UnprocessableException>()).Which;
        e.Code.Should().Be(code);
        return e;
    }

    [Fact]
    public async Task Priced_PerSeller_TotalIsTheExactSum_AndTheZonesAreLoadedOnce()
    {
        var zones = new Zones(Zone());
        var q = await Quote(zones, false, District, 1, Line(SellerA, 2), Line(SellerB, 3), Line(SellerB, 1));   // 1 kg and 2 kg

        q.PerSeller.Select(s => (s.SellerId, s.ShippingMinor)).Should().Equal((SellerA, 6_000L), (SellerB, 12_000L));
        q.TotalMinor.Should().Be(18_000);
        zones.Calls.Should().Be(1);
    }

    [Theory]
    [InlineData(99_999, 6_000)]
    [InlineData(100_000, 0)]
    public async Task FreeShipping_StartsAtTheThreshold_OfTheGoodsTotal(long goods, long expected)
    {
        var q = await Quote(new Zones(Zone(freeFrom: 100_000)), false, District, goods, Line(SellerA, 1));
        (q.TotalMinor, q.FreeShippingApplied).Should().Be((expected, expected == 0));
    }

    [Fact]
    public async Task WithNoZoneAtAll_TheDevelopmentSwitchShipsFree_AndProductionRefuses()
    {
        (await Quote(new Zones(), true, null, 1, Line(SellerA, 1), Line(SellerB, 1))).PerSeller
            .Should().OnlyContain(s => s.ShippingMinor == 0).And.HaveCount(2);
        await Refused(() => Quote(new Zones(), false, District, 1, Line(SellerA, 1)), IOrderShippingQuoter.NotAvailable);
    }

    [Fact]
    public async Task OnceAZoneExists_TheSwitchNoLongerHelps_AnUnservedOrUnknownDestination()
    {
        await Refused(() => Quote(new Zones(Zone()), true, Guid.NewGuid(), 1, Line(SellerA, 1)), IOrderShippingQuoter.NotAvailable);
        await Refused(() => Quote(new Zones(Zone()), true, null, 1, Line(SellerA, 1)), IOrderShippingQuoter.NotAvailable);
    }

    [Fact]
    public async Task QuoteOnly_IsRefused_NamingTheSku_AndWinsOverFreeShipping()
    {
        var e = await Refused(() => Quote(new Zones(Zone(freeFrom: 1, heavy: 400, oversizeMm: 500)), false, District, 1_000_000,
            Line(SellerA, 1, sku: "HEAVY-1"), Line(SellerB, 1, kg: 0.1m, sku: "LONG-1", sideCm: 50.1m)), IOrderShippingQuoter.QuoteOnly);
        e.Details.Should().BeEquivalentTo([new ValidationError(ShippingCalculator.HeavyUnit, "HEAVY-1"), new ValidationError(ShippingCalculator.Oversize, "LONG-1")]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task AMissingWeight_IsRefusedWithTheSku_NeverFree(decimal kg)
    {
        var e = await Refused(() => Quote(new Zones(Zone(freeFrom: 1)), false, District, 10, Line(SellerA, 1, kg: kg, sku: "NOWEIGHT-7")),
            IOrderShippingQuoter.WeightMissing);
        e.Message.Should().Contain("NOWEIGHT-7");
        e.Details.Should().Equal(new ValidationError("sku", "NOWEIGHT-7"));
    }

    [Fact]
    public async Task UnknownSides_AreNotOversize()
    {
        var q = await Quote(new Zones(Zone(oversizeMm: 10)), false, District, 1, Line(SellerA, 1, sideCm: null), Line(SellerA, 1, sideCm: 0m));
        q.TotalMinor.Should().Be(6_000);
    }

    private static IConfiguration Config(string? allowFree) => new ConfigurationBuilder()
        .AddInMemoryCollection(allowFree is null ? [] : [new KeyValuePair<string, string?>("Shipping:AllowFreeWhenNoZones", allowFree)]).Build();

    [Theory]
    [InlineData(null, true, true)]
    [InlineData(null, false, false)]
    [InlineData("false", true, false)]
    [InlineData("false", false, false)]
    [InlineData("true", true, true)]
    public void TheSwitch_DefaultsOnOnlyInDevelopmentOrTesting(string? configured, bool devOrTesting, bool expected) =>
        ShippingOptions.From(Config(configured), devOrTesting).AllowFreeWhenNoZones.Should().Be(expected);

    [Theory]
    [InlineData("true", "only allowed in Development or Testing")]
    [InlineData("yes", "must be true or false")]
    public void OutsideDevelopment_TheSwitchCannotBeTurnedOn(string configured, string message) =>
        FluentActions.Invoking(() => ShippingOptions.From(Config(configured), developmentOrTesting: false))
            .Should().Throw<InvalidOperationException>().WithMessage($"*{message}*");

    [Theory]
    [InlineData(null, null)]
    [InlineData("   ", null)]
    [InlineData("  PO/2026-001 #7_a.b  ", "PO/2026-001 #7_a.b")]
    [InlineData("PO-000000001-000000002-000000003-0000004", "PO-000000001-000000002-000000003-0000004")]   // exactly 40
    public void APurchaseOrderNumber_IsTrimmed_AndBlankIsNone(string? input, string? expected) =>
        Order.NormalisePurchaseOrderNumber(input).Should().Be(expected);

    [Theory]
    [InlineData("PO-000000001-000000002-000000003-00000045")]   // 41
    [InlineData("PO\t1")]
    [InlineData("PO\r\n1")]
    [InlineData("PO\u001b[31m")]
    [InlineData("=1+1")]
    [InlineData("<b>PO</b>")]
    [InlineData("PO;DROP")]
    [InlineData("পিও-১২")]
    [InlineData("PO​1")]
    public void APurchaseOrderNumber_WithAnythingElse_IsRefused(string input) =>
        FluentActions.Invoking(() => Order.NormalisePurchaseOrderNumber(input)).Should().Throw<ValidationException>()
            .Which.Errors.Should().ContainSingle(e => e.Field == "purchaseOrderNumber");
}
