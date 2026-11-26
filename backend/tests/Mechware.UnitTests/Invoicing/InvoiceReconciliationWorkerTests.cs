using FluentAssertions;
using Mechware.Application.Invoicing;
using Mechware.Infrastructure;
using Mechware.Infrastructure.Workers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Mechware.UnitTests.Invoicing;

/// <summary>E6.3d: the nightly invoice check is hosted with the other workers (Worker host only) and runs at 03:00 Dhaka.</summary>
public class InvoiceReconciliationWorkerTests
{
    [Fact]
    public void TheWorker_IsHostedWithTheOtherWorkers_AndTheServiceIsRegistered()
    {
        new ServiceCollection().AddCatalogWorkers().Should()
            .Contain(d => d.ServiceType == typeof(IHostedService) && d.ImplementationType == typeof(InvoiceReconciliationWorker));
        new ServiceCollection().AddCatalogServices().Should().Contain(d => d.ServiceType == typeof(IInvoiceReconciliationService));
    }

    [Theory]
    [InlineData("2026-11-16T20:59:59Z", "2026-11-16T21:00:00Z")]
    [InlineData("2026-11-16T21:00:00Z", "2026-11-17T21:00:00Z")]
    [InlineData("2026-11-16T18:00:00Z", "2026-11-16T21:00:00Z")]   // 00:00 Dhaka: the same night
    [InlineData("2026-11-17T03:00:00Z", "2026-11-17T21:00:00Z")]
    public void TheWorker_RunsNightlyAt0300Dhaka(string now, string next) =>
        InvoiceReconciliationWorker.NextRunUtc(DateTime.Parse(now, null, System.Globalization.DateTimeStyles.AdjustToUniversal))
            .Should().Be(DateTime.Parse(next, null, System.Globalization.DateTimeStyles.AdjustToUniversal));
}
