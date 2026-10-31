using ShopVerse.Infrastructure.Payments;
using ShopVerse.Application.Payments;
using ShopVerse.Infrastructure.Orders;
using ShopVerse.Application.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ShopVerse.Application.Catalog;
using ShopVerse.Application.Approvals;
using ShopVerse.Application.Common;
using ShopVerse.Application.Companies;
using ShopVerse.Application.Inventory;
using ShopVerse.Infrastructure.Approvals;
using ShopVerse.Infrastructure.Caching;
using ShopVerse.Infrastructure.Catalog;
using ShopVerse.Infrastructure.Companies;
using ShopVerse.Infrastructure.Inventory;
using ShopVerse.Infrastructure.Persistence.Outbox;
using ShopVerse.Infrastructure.Workers;

namespace ShopVerse.Infrastructure;

public static class DependencyInjection
{
    /// <summary>Catalog, inventory, outbox and storefront-cache services. Requires <c>IConnectionMultiplexer</c> and the DbContext.</summary>
    public static IServiceCollection AddCatalogServices(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<DomainEventOutboxInterceptor>();
        services.TryAddSingleton<IDistributedLockProvider, RedLockProvider>();
        services.TryAddSingleton<IStorefrontCacheInvalidator, RedisStorefrontCacheInvalidator>();
        services.TryAddScoped<IInventoryService, InventoryService>();
        services.TryAddScoped<IStorefrontCatalogReader, StorefrontCatalogReader>();
        services.TryAddScoped<ICompanyService, CompanyService>();
        services.TryAddScoped<IApprovalService, ApprovalService>();
        services.TryAddScoped<IOrderService, OrderService>();
        services.TryAddScoped<IPaymentService, PaymentService>();

        services.AddScoped<IOutboxPublisher, SearchSyncQueuePublisher>();
        services.AddScoped<IOutboxPublisher, StorefrontCacheInvalidationPublisher>();
        services.TryAddScoped<OutboxRelay>();
        return services;
    }

    /// <summary>Adds the hosted workers (run these from <c>ShopVerse.Worker</c>, not from every API replica).</summary>
    public static IServiceCollection AddCatalogWorkers(this IServiceCollection services)
    {
        services.AddHostedService<ReservationExpiryWorker>();
        services.AddHostedService<UnpaidOrderExpiryWorker>();
        services.AddHostedService<OutboxRelayWorker>();
        return services;
    }
}
