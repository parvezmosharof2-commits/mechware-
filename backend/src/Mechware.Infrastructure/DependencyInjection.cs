using Mechware.Infrastructure.Payments;
using Mechware.Application.Payments;
using Mechware.Infrastructure.Orders;
using Mechware.Application.Orders;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Mechware.Application.Catalog;
using Mechware.Application.Approvals;
using Mechware.Application.Common;
using Mechware.Application.Companies;
using Mechware.Application.Inventory;
using Mechware.Infrastructure.Approvals;
using Mechware.Infrastructure.Caching;
using Mechware.Infrastructure.Catalog;
using Mechware.Infrastructure.Companies;
using Mechware.Infrastructure.Documents;
using Microsoft.Extensions.Options;
using Mechware.Infrastructure.Inventory;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.Infrastructure.Workers;

namespace Mechware.Infrastructure;

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
        services.TryAddScoped<ICatalogAdminService, CatalogAdminService>();
        services.TryAddScoped<ICompanyService, CompanyService>();
        services.TryAddScoped<IApprovalService, ApprovalService>();
        services.TryAddScoped<IOrderService, OrderService>();
        services.TryAddScoped<IPaymentService, PaymentService>();
        services.TryAddScoped<IRefundService, RefundService>();

        services.AddScoped<IOutboxPublisher, SearchSyncQueuePublisher>();
        services.AddScoped<IOutboxPublisher, StorefrontCacheInvalidationPublisher>();
        services.TryAddScoped<OutboxRelay>();
        return services;
    }

    /// <summary>The largest upload the API accepts; the endpoint's request limit is set to this, so a configured limit above it is refused.</summary>
    public const long MaxUploadRequestBytes = 64L * 1024 * 1024;

    /// <summary>
    /// Product documents: the object store and the malware scanner. Outside Development both must be configured for real: a
    /// directory on a persistent volume (<c>Documents:LocalPath</c>) and a ClamAV daemon (<c>Antivirus:Host</c>). Development
    /// and tests fall back to a temp directory and an EICAR-only scanner, which is not a virus scanner.
    /// </summary>
    public static IServiceCollection AddProductDocuments(
        this IServiceCollection services, DocumentOptions documents, AntivirusOptions antivirus, bool requireRealSetup)
    {
        if (documents.MaxDatasheetBytes <= 0 || documents.MaxCadBytes <= 0 || documents.MaxCertificateBytes <= 0)
            throw new InvalidOperationException("Documents: every size limit must be above zero.");
        if (documents.LargestLimit > MaxUploadRequestBytes - 1024 * 1024)
            throw new InvalidOperationException($"Documents: the largest size limit must stay below {(MaxUploadRequestBytes - 1024 * 1024) / (1024 * 1024)} MB.");

        bool haveScanner = !string.IsNullOrWhiteSpace(antivirus.Host);
        if (requireRealSetup && string.IsNullOrWhiteSpace(documents.LocalPath))
            throw new InvalidOperationException("Documents:LocalPath is not set; point Documents__LocalPath at a directory on a persistent volume.");
        if (requireRealSetup && !haveScanner)
            throw new InvalidOperationException("Antivirus:Host is not set; uploads need a ClamAV daemon (Antivirus__Host, Antivirus__Port).");

        string path = string.IsNullOrWhiteSpace(documents.LocalPath) ? Path.Combine(Path.GetTempPath(), "mechware-documents") : documents.LocalPath;
        services.AddSingleton(Options.Create(documents));
        services.AddSingleton<IObjectStorage>(_ => new LocalObjectStorage(path));
        services.AddSingleton<IVirusScanner>(_ => haveScanner
            ? new ClamAvScanner(antivirus.Host!, antivirus.Port, TimeSpan.FromSeconds(Math.Clamp(antivirus.TimeoutSeconds, 1, 600)))
            : new DevelopmentSignatureScanner());
        services.TryAddScoped<IProductDocumentService, ProductDocumentService>();
        return services;
    }

    /// <summary>Adds the hosted workers (run these from <c>Mechware.Worker</c>, not from every API replica).</summary>
    public static IServiceCollection AddCatalogWorkers(this IServiceCollection services)
    {
        services.AddHostedService<ReservationExpiryWorker>();
        services.AddHostedService<UnpaidOrderExpiryWorker>();
        services.AddHostedService<OutboxRelayWorker>();
        return services;
    }
}
