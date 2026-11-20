using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Mechware.Infrastructure;
using Mechware.Infrastructure.Orders;
using Mechware.Infrastructure.Workers;

namespace Mechware.UnitTests.Orders;

/// <summary>E7.3: the stock sweep's settings and its registration in the Worker host.</summary>
public class StockReconciliationOptionsTests
{
    private static StockReconciliationOptions From(params (string Key, string Value)[] values) => StockReconciliationOptions.From(
        new ConfigurationBuilder().AddInMemoryCollection(values.Select(v => new KeyValuePair<string, string?>("StockReconciliation:" + v.Key, v.Value))).Build());

    [Fact]
    public void TheDefaults_AreTenMinutesFiveMinutesOfGraceAndBatchesOf100()
    {
        From().Should().BeEquivalentTo(new { Interval = TimeSpan.FromMinutes(10), GracePeriod = TimeSpan.FromMinutes(5), BatchSize = 100 });
        From(("Interval", "00:02:00"), ("GracePeriod", "00:03:00"), ("BatchSize", "20"))
            .Should().BeEquivalentTo(new { Interval = TimeSpan.FromMinutes(2), GracePeriod = TimeSpan.FromMinutes(3), BatchSize = 20 });
    }

    [Theory]
    [InlineData("GracePeriod", "00:00:00")]
    [InlineData("GracePeriod", "00:00:30")]
    [InlineData("Interval", "00:00:00")]
    [InlineData("BatchSize", "0")]
    [InlineData("BatchSize", "1001")]
    public void ASettingThatWouldRaceRequestsOrNeverRun_StopsTheHost(string key, string value) =>
        FluentActions.Invoking(() => From((key, value))).Should().Throw<InvalidOperationException>();

    [Fact]
    public void TheWorker_IsHostedWithTheOtherWorkers_WithItsServiceAndDefaults()
    {
        var services = new ServiceCollection().AddCatalogWorkers();
        services.Should().Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(StockReconciliationWorker));
        services.Should().Contain(d => d.ServiceType == typeof(StockReconciliationService));
        services.Should().Contain(d => d.ServiceType == typeof(StockReconciliationOptions));
    }
}
