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
using Mechware.Application.Pdf;
using Mechware.Infrastructure.Pdf;
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
        services.TryAddScoped<IProductAdminService, ProductAdminService>();
        services.TryAddScoped<IAdminVariantLookup, AdminVariantLookup>();
        services.TryAddScoped<IAdminCatalogLookup, AdminCatalogLookup>();
        services.TryAddSingleton(new Mechware.Application.Catalog.CatalogImportOptions());
        services.TryAddScoped<Mechware.Application.Catalog.ICatalogImportLookup, CatalogImportLookup>();
        services.TryAddScoped<Mechware.Application.Catalog.ICatalogImportValidator, CatalogImportValidator>();
        services.TryAddScoped<Mechware.Application.Catalog.ICatalogImportCommitter, CatalogImportCommitter>();
        services.TryAddScoped<ICompanyService, CompanyService>();
        services.TryAddScoped<IApprovalService, ApprovalService>();
        // Legal delivery periods: defaults unless the host registered options read (and validated) from Delivery:* first (the API does).
        services.TryAddSingleton(new Orders.DeliveryOptions());
        services.TryAddScoped<Mechware.Application.Orders.IOrderDeadlineService, Orders.OrderDeadlineService>();
        services.TryAddScoped<Mechware.Application.Orders.IStaffOrderReadService, Orders.StaffOrderReadService>();
        services.TryAddScoped<Mechware.Application.Audit.IAuditLogReadService, Persistence.AuditLogReadService>();
        services.TryAddScoped<Mechware.Application.Orders.IDeliveryDeadlineCalculator, Orders.DeliveryDeadlineCalculator>();
        // Credit settlement defaults pending owner confirmation, unless the host registered options read from Credit:* first.
        services.TryAddSingleton(new Mechware.Application.Credit.CreditOptions());
        services.TryAddScoped<Mechware.Application.Credit.ICreditPaymentService, Credit.CreditPaymentService>();
        services.TryAddScoped<Mechware.Application.Credit.ICreditLedger, Credit.CreditLedgerService>();
        services.TryAddScoped<Mechware.Application.Credit.ICreditAdjustmentService, Credit.CreditAdjustmentService>();
        services.TryAddScoped<Mechware.Application.Credit.ICreditHoldService, Credit.CreditHoldService>();
        services.TryAddScoped<Mechware.Application.Credit.ICreditReadService, Credit.CreditReadService>();
        services.TryAddScoped<Mechware.Application.Credit.IBuyerCreditService, Credit.BuyerCreditService>();
        services.TryAddScoped<Mechware.Application.Credit.ICreditReconciliationService, Credit.CreditReconciliationService>();
        // Invoicing (E6.2): gap-free numbers, taken inside the issuing transaction as its last lock.
        services.TryAddScoped<Mechware.Application.Invoicing.IInvoiceNumberAllocator, Invoicing.InvoiceNumberAllocator>();
        // E6.3c: the 6.3 issued at shipment. Off unless the host registered validated Invoicing:* options first (Api, Worker).
        services.TryAddSingleton(new Mechware.Domain.Invoicing.InvoicingOptions());
        services.TryAddScoped<Mechware.Application.Invoicing.IInvoiceIssuer, Invoicing.InvoiceIssuer>();
        // E6.5b: credit-note requests and their four-eyes approval (the 6.7 + its money in one transaction).
        services.TryAddScoped<Mechware.Application.Invoicing.ICreditNoteService, Invoicing.CreditNoteService>();
        // E6.3d: the nightly invoice check (alert + audit, never repairs); hosted by AddCatalogWorkers.
        services.TryAddScoped<Mechware.Application.Invoicing.IInvoiceReconciliationService, Invoicing.InvoiceReconciliationService>();
        // E6.8a: buyer and staff invoice reads (one class, two interfaces).
        services.TryAddScoped<Invoicing.InvoiceReadService>();
        services.TryAddScoped<Mechware.Application.Invoicing.IBuyerInvoiceReadService>(sp => sp.GetRequiredService<Invoicing.InvoiceReadService>());
        services.TryAddScoped<Mechware.Application.Invoicing.IStaffInvoiceReadService>(sp => sp.GetRequiredService<Invoicing.InvoiceReadService>());
        // RFQs (D4.5): options from Quotes:* when the host registered them first, the owner defaults otherwise.
        services.TryAddSingleton(new Mechware.Domain.Quotes.QuoteOptions());
        services.TryAddScoped<Mechware.Application.Quotes.IStaffQuoteService, Quotes.StaffQuoteService>();
        services.TryAddScoped<Mechware.Application.Quotes.IStaffQuoteLookups, Quotes.StaffQuoteLookups>();
        services.TryAddScoped<Mechware.Application.Quotes.IQuoteAcceptanceGate, Quotes.QuoteAcceptanceGate>();
        services.TryAddScoped<Mechware.Application.Quotes.IBuyerQuoteService, Quotes.BuyerQuoteService>();
        services.TryAddSingleton(new Mechware.Application.Quotes.QuotePdfOptions());
        services.TryAddScoped<Mechware.Application.Quotes.IQuoteTermsService, Quotes.QuoteTermsService>();
        services.TryAddScoped<IOrderService, OrderService>();
        services.TryAddScoped<Mechware.Application.Orders.IOrderCancellationService, Orders.OrderCancellationService>();
        services.TryAddScoped<Mechware.Application.Orders.IOrderFulfilmentService, Orders.OrderFulfilmentService>();
        // F2.1 return requests: 7-day window and no non-returnable category unless the host registered Returns:* first.
        services.TryAddSingleton(new Mechware.Application.Orders.ReturnOptions());
        services.TryAddScoped<Mechware.Application.Orders.IReturnRequestService, Orders.ReturnRequestService>();
        services.TryAddScoped<Mechware.Application.Orders.IQuickOrderValidator, Orders.QuickOrderValidator>();
        services.TryAddScoped<Mechware.Application.Shipping.IShippingRateProvider, Shipping.ShippingRateProvider>();
        // Fail closed unless the host registered its own options first (the API does, from Shipping:AllowFreeWhenNoZones).
        services.TryAddSingleton(new Shipping.ShippingOptions());
        services.TryAddScoped<Mechware.Application.Shipping.IOrderShippingQuoter, Shipping.OrderShippingQuoter>();
        services.TryAddScoped<Mechware.Application.Shipping.IShippingZoneService, Shipping.ShippingZoneService>();
        // Below cost refused unless the host registered its own options first (the API does, from Pricing:AllowBelowCost).
        services.TryAddSingleton(new Pricing.PricingOptions());
        services.TryAddScoped<Mechware.Application.Pricing.IPriceResolver, Pricing.PriceListResolver>();
        services.TryAddScoped<Mechware.Application.Pricing.IPriceListService, Pricing.PriceListService>();
        services.TryAddScoped<Mechware.Application.Pricing.IPriceCheckFacts, Pricing.PriceCheckFactsReader>();
        services.TryAddScoped<Mechware.Application.Pricing.IMarginFloorService, Pricing.MarginFloorService>();
        services.TryAddScoped<Mechware.Application.Pricing.ICompanyPriceService, Pricing.CompanyPriceService>();
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
    public static IServiceCollection AddNotifications(this IServiceCollection services, EmailOptions options, bool requireRealSetup, string? storefrontBaseUrl = null, string? salesInboxEmail = null)
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
        services.TryAddSingleton(new StorefrontLinks(NotificationFormat.NormalizeBaseUrl(storefrontBaseUrl, requireRealSetup)));
        services.TryAddScoped<NotificationDispatcher>();
        services.AddScoped<IOutboxPublisher, NotificationOutboxPublisher>();
        services.TryAddSingleton(new QuoteNoticeOptions(salesInboxEmail));
        services.AddScoped<IOutboxPublisher, QuoteNotificationPublisher>();
        services.TryAddSingleton<INotificationTemplates, EmbeddedTemplates>();
        if (haveHost) services.TryAddSingleton<INotificationSender, SmtpEmailSender>();
        else services.TryAddSingleton<INotificationSender, LoggingEmailSender>();
        return services;
    }

    /// <summary>The PDF renderer (Skia + HarfBuzz, embedded Hind Siliguri). Stateless; no network or browser.</summary>
    public static IServiceCollection AddPdfRendering(this IServiceCollection services)
    {
        services.TryAddSingleton<IPdfRenderer, SkiaPdfRenderer>();
        // Quote PDFs (D4.11) need the renderer and the object store (AddProductDocuments): API host only, the Worker renders nothing yet.
        services.TryAddScoped<Mechware.Application.Quotes.IQuotePdfService, Quotes.QuotePdfService>();
        // Invoice PDFs (E6.4b): lazy download in the API, eager render after COMMIT in the Worker (outbox handler); both need the renderer and the object store.
        services.TryAddScoped<Mechware.Application.Invoicing.IInvoicePdfService, Invoicing.InvoicePdfService>();
        services.AddScoped<IOutboxPublisher, Invoicing.InvoicePdfPublisher>();
        return services;
    }

    /// <summary>
    /// The cart store (Redis) and the signed guest-cart token (E1.2). Requires <c>IConnectionMultiplexer</c>. Outside
    /// Development and Testing <c>GuestCart:TokenKey</c> must be a real secret (at least 32 random bytes, base64) and not the
    /// published development key, or startup is refused here.
    /// </summary>
    public static IServiceCollection AddCartStore(this IServiceCollection services, Microsoft.Extensions.Configuration.IConfiguration configuration, bool requireRealSetup)
    {
        var (tokenKey, previousTokenKeys) = Cart.GuestCartTokenOptions.From(configuration).ResolveKeys(requireRealSetup);
        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<Mechware.Application.Cart.IGuestCartTokenService>(sp =>
            new Cart.GuestCartTokenService(tokenKey, previousTokenKeys, sp.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<Mechware.Application.Cart.ICartStore, Cart.RedisCartStore>();
        services.TryAddSingleton<Mechware.Application.Cart.IGuestCartCreationLimiter>(sp => new Cart.GuestCartCreationLimiter(
            (int.TryParse(configuration["Cart:NewGuestCartsPerIpPerHour"], out int perHour) ? perHour : Cart.GuestCartCreationLimiter.DefaultPerHour), sp.GetRequiredService<TimeProvider>()));
        services.TryAddScoped<Mechware.Application.Cart.ICartCatalogReader, Cart.CartCatalogReader>();
        services.TryAddScoped<Mechware.Application.Cart.ICartService, Mechware.Application.Cart.CartService>();
        services.TryAddScoped<Mechware.Application.Cart.ICartMergeService, Mechware.Application.Cart.CartMergeService>();
        // Buyer RFQs (D4.5) read the cart, so they live with the cart: the Worker host has no cart and never takes RFQs.
        services.TryAddScoped<Mechware.Application.Quotes.IBuyerRfqService, Quotes.BuyerRfqService>();
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
        services.AddHostedService<CreditHoldWorker>();
        services.AddHostedService<CreditReconciliationWorker>();
        services.AddHostedService<InvoiceReconciliationWorker>();
        services.TryAddScoped<Quotes.QuoteNoticeService>();
        services.AddHostedService<QuoteNoticeWorker>();
        // E7.3 stock sweep: options from StockReconciliation:* when the host registered them first, the defaults otherwise.
        services.TryAddSingleton(new Orders.StockReconciliationOptions());
        services.TryAddScoped<Orders.StockReconciliationService>();
        services.AddHostedService<StockReconciliationWorker>();
        return services;
    }
}
