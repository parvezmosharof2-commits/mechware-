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
using Mechware.Infrastructure.Search;
using Mechware.Infrastructure.Notifications;
using Mechware.Application.Notifications;
using Mechware.Infrastructure.Workers;
using Mechware.Application.Search;
using Microsoft.Extensions.Logging;

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
        services.TryAddSingleton<Mechware.Application.Search.ISearchReindexQueue, Mechware.Infrastructure.Search.RedisSearchReindexQueue>();
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

    /// <summary>
    /// The search engine (Meilisearch) behind <c>ISearchProvider</c>. Outside Development and Testing the key must be set: a
    /// production engine with no key would be open to anyone who can reach it.
    /// </summary>
    public static IServiceCollection AddSearch(this IServiceCollection services, SearchOptions options, bool requireRealSetup)
    {
        if (!Uri.TryCreate(options.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException("Search:Url must be an absolute http(s) address (Search__Url).");
        if (requireRealSetup && string.IsNullOrWhiteSpace(options.ApiKey))
            throw new InvalidOperationException("Search:ApiKey is not set; set Search__ApiKey to the Meilisearch key.");
        if (options.IndexName is not { Length: > 0 and <= 64 } || !options.IndexName.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-'))
            throw new InvalidOperationException("Search:IndexName may only contain letters, digits, '_' and '-' (at most 64).");

        services.AddSingleton(options);
        services.TryAddSingleton<ISearchProvider>(sp => new MeilisearchSearchProvider(
            // Recycled connections follow DNS changes (engine failover, blue/green deploys).
            new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }), options, sp.GetRequiredService<ILogger<MeilisearchSearchProvider>>()));
        services.TryAddSingleton<IStorefrontSearch, StorefrontSearchService>();
        return services;
    }

    /// <summary>
    /// Email (SMTP) and the bilingual template engine. Outside Development and Testing the SMTP host and the From address must be set.
    /// With no host in Development/Testing, messages are only logged.
    /// </summary>
    public static IServiceCollection AddNotifications(this IServiceCollection services, EmailOptions options, bool requireRealSetup)
    {
        bool haveHost = !string.IsNullOrWhiteSpace(options.Host);
        if (requireRealSetup && !haveHost)
            throw new InvalidOperationException("Email:Host is not set; set Email__Host to the SMTP server.");
        if (requireRealSetup && string.IsNullOrWhiteSpace(options.FromAddress))
            throw new InvalidOperationException("Email:FromAddress is not set; set Email__FromAddress to the sending address.");
        if (haveHost && !System.Net.Mail.MailAddress.TryCreate(options.FromAddress, out _))
            throw new InvalidOperationException("Email:FromAddress must be a valid email address.");
        if (options.Port is < 1 or > 65535)
            throw new InvalidOperationException("Email:Port must be between 1 and 65535.");

        services.AddSingleton(options);
        services.TryAddSingleton<INotificationTemplates, EmbeddedTemplates>();
        if (haveHost) services.TryAddSingleton<INotificationSender, SmtpEmailSender>();
        else services.TryAddSingleton<INotificationSender, LoggingEmailSender>();
        return services;
    }

    /// <summary>The search indexer and its worker. Requires <see cref="AddSearch"/>, the DbContext and Redis. Host it once (Worker).</summary>
    public static IServiceCollection AddSearchIndexing(this IServiceCollection services)
    {
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddScoped<ProductSearchDocumentLoader>();
        services.TryAddScoped<SearchIndexer>();
        services.AddHostedService<SearchIndexWorker>();
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
