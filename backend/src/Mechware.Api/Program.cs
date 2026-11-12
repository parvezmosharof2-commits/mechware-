using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Options;
using Microsoft.OpenApi;
using Serilog;
using Mechware.Api.Authentication;
using Mechware.Api.Authorization;
using Mechware.Api.Configuration;
using Mechware.Api.Health;
using Mechware.Api.Middleware;
using Mechware.Api.Services;
using Mechware.Application.Identity;
using Mechware.Infrastructure;
using Mechware.Infrastructure.Identity;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.Infrastructure.Search;
using Mechware.Infrastructure.Security;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// 1. Serilog Structured Logging
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

// 2. Strongly Typed Configuration with Startup Validation
// One database for everything: ConnectionStrings:DefaultConnection, else the older Database:ConnectionString (MechwareDatabase).
string databaseConnection = MechwareDatabase.ConnectionString(builder.Configuration);
builder.Services.AddOptions<DatabaseOptions>()
    .BindConfiguration(DatabaseOptions.SectionName)
    .PostConfigure(o => o.ConnectionString = databaseConnection)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<RedisOptions>()
    .BindConfiguration(RedisOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddOptions<JwtOptions>()
    .BindConfiguration(JwtOptions.SectionName)
    .ValidateDataAnnotations()
    .Validate(o => builder.Environment.IsDevelopment() || !JwtOptions.PublishedDevelopmentSecrets.Contains(o.Secret),
        "JWT Secret is a published development value; set Jwt__Secret from the secret store.")
    .ValidateOnStart();

// Object storage is optional (no feature uses it yet); when Storage:ServiceUrl is set it must be complete.
builder.Services.AddOptions<StorageOptions>()
    .BindConfiguration(StorageOptions.SectionName)
    .Validate(o => o.IsValid(), StorageOptions.InvalidMessage)
    .ValidateOnStart();
bool objectStorageConfigured = (builder.Configuration.GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new()).IsConfigured;

// 2.0a Application-level encryption of business identifiers (threat model v1, H4). Outside Development the key must come
// from the secret store: the development key is published in this repository.
{
    // Rotation: the new key goes in FieldKey and the old one in PreviousFieldKeys (comma-separated) until the backfill has
    // rewritten every row; then the old key is removed.
    Mechware.Infrastructure.Security.FieldEncryption.ConfigureFrom(
        builder.Configuration["Encryption:FieldKey"], builder.Configuration["Encryption:PreviousFieldKeys"],
        requireRealKey: !(builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing"));
}
builder.Services.AddHostedService<Mechware.Infrastructure.Workers.FieldEncryptionBackfillWorker>();

// 2.0b Product documents (datasheets, CAD, certificates): a persistent directory and a real malware scanner outside Development.
builder.Services.AddProductDocuments(
    builder.Configuration.GetSection(Mechware.Infrastructure.Documents.DocumentOptions.SectionName).Get<Mechware.Infrastructure.Documents.DocumentOptions>() ?? new(),
    builder.Configuration.GetSection(Mechware.Infrastructure.Documents.AntivirusOptions.SectionName).Get<Mechware.Infrastructure.Documents.AntivirusOptions>() ?? new(),
    requireRealSetup: !(builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing"));

// 2.1 Singleton ConnectionMultiplexer (CRIT-01: prevents socket exhaustion DoS)
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var redisConn = builder.Configuration.GetConnectionString("Redis")
        ?? builder.Configuration.GetSection(RedisOptions.SectionName)["ConnectionString"]
        ?? "localhost:6379";
    var options = ConfigurationOptions.Parse(redisConn);
    options.AbortOnConnectFail = false;
    options.ConnectTimeout = 3000;
    return ConnectionMultiplexer.Connect(options);
});

// 2.2 Forwarded Headers Options (HIGH-03: reverse-proxy client IP for rate limiting)
// Behind Cloudflare / a load balancer set ForwardedHeaders:KnownNetworks (see ForwardedHeadersSettings), or every visitor
// shares the proxy's address and its rate limits.
{
    var forwarded = new ForwardedHeadersOptions();
    ForwardedHeadersSettings.Apply(forwarded, builder.Configuration,
        trustAnyProxy: builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing");
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = forwarded.ForwardedHeaders;
        options.ForwardLimit = forwarded.ForwardLimit;
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
        foreach (var network in forwarded.KnownIPNetworks) options.KnownIPNetworks.Add(network);
        foreach (var proxy in forwarded.KnownProxies) options.KnownProxies.Add(proxy);
    });
}

// 2.3 Phase 3 Identity, Persistence & RBAC Registration
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, HttpContextCurrentUserService>();
builder.Services.AddScoped<AuditLogSaveChangesInterceptor>();

builder.Services.AddDbContext<MechwareDbContext>((sp, options) =>
{
    options.UseNpgsql(databaseConnection)
        .AddInterceptors(
            sp.GetRequiredService<AuditLogSaveChangesInterceptor>(),
            sp.GetRequiredService<DomainEventOutboxInterceptor>());
});

// 2.3a Data Protection keys in the database, shared by every instance and kept across deploys. With
// DataProtection:CertificatePath (PKCS#12 with private key) they are encrypted at rest; without it they are plain XML.
bool dataProtectionKeysEncrypted = builder.Services.AddPersistedDataProtection(
    builder.Configuration["DataProtection:CertificatePath"], builder.Configuration["DataProtection:CertificatePassword"]);

// 2.4 Phase 4 Catalog, Inventory, Outbox and storefront cache (hosted workers live in Mechware.Worker)
builder.Services.AddCatalogServices();

// Storefront search (C6.3): the API only queries the engine; indexing runs in the Worker.
var searchOptions = builder.Configuration.GetSection(SearchOptions.SectionName).Get<SearchOptions>() ?? new SearchOptions();
builder.Services.AddSearch(searchOptions, requireRealSetup: !(builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing"));

builder.Services.AddNotifications(
    builder.Configuration.GetSection(Mechware.Infrastructure.Notifications.EmailOptions.SectionName).Get<Mechware.Infrastructure.Notifications.EmailOptions>() ?? new(),
    requireRealSetup: !(builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing"),
    storefrontBaseUrl: builder.Configuration["Storefront:BaseUrl"]);

builder.Services.AddPdfRendering();

builder.Services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
builder.Services.AddSingleton<ITotpService, TotpService>();
builder.Services.AddSingleton<ISmsProvider, LoggingSmsProvider>();
// OTP SMS abuse limits (F3a.2a): per number, per IP, global breaker, in Redis; fails closed when Redis is down.
builder.Services.AddOptions<OtpSmsLimitsOptions>()
    .BindConfiguration(OtpSmsLimitsOptions.SectionName)
    .Validate(o => o.IsValid(), "OtpSms limits must be positive and KeyPrefix must be set without braces.")
    .ValidateOnStart();
builder.Services.AddSingleton<IOtpSmsGuard, RedisOtpSmsGuard>();
builder.Services.AddSingleton<ITokenService>(sp =>
{
    JwtOptions jwtOpts = sp.GetRequiredService<IOptions<JwtOptions>>().Value;
    return new JwtTokenService(
        jwtOpts.Secret,
        jwtOpts.Issuer,
        jwtOpts.Audience,
        jwtOpts.AccessTokenExpiryMinutes);
});
// Demo accounts with published credentials exist only in Development (see IdentitySeedPolicy).
builder.Services.AddSingleton(IdentitySeedPolicy.ForEnvironment(builder.Environment.EnvironmentName));

// The in-process mock gateway exists only where demo accounts do. In any other environment no provider is registered
// until a real adapter is configured, so there is no way to "pay" through a gateway with a published secret.
if (IdentitySeedPolicy.ForEnvironment(builder.Environment.EnvironmentName).SeedDemoAccounts)
{
    builder.Services.AddSingleton<Mechware.Application.Payments.IPaymentProvider, Mechware.Infrastructure.Payments.MockPaymentProvider>();
}

// Asks the providers about payments whose webhook never arrived (threat model v2, P4). It lives in the API host because that
// is where the payment providers are registered; with no provider registered it finds nothing to ask.
builder.Services.AddHostedService<Mechware.Infrastructure.Workers.PaymentReconciliationWorker>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<Mechware.Application.Audit.IAuditChain, Mechware.Infrastructure.Persistence.AuditChain>();
builder.Services.AddHostedService<Mechware.Infrastructure.Workers.AuditChainWorker>();
// First administrator for environments without demo accounts; secrets come from deployment configuration only.
var bootstrapAdmin = BootstrapAdminSettings.FromValues(
    builder.Configuration["Identity:BootstrapAdmin:Email"],
    builder.Configuration["Identity:BootstrapAdmin:FullName"],
    builder.Configuration["Identity:BootstrapAdmin:Phone"],
    builder.Configuration["Identity:BootstrapAdmin:Password"],
    builder.Configuration["Identity:BootstrapAdmin:TotpSecret"]);
builder.Services.AddScoped<IdentitySeeder>();
builder.Services.AddScoped<DemoAccountGuard>();
builder.Services.AddScoped<IIdentityService, IdentityService>();
builder.Services.AddScoped<ISessionValidator, SessionValidator>();
// Google sign-in exists only where a client id is configured; without one the endpoint answers GOOGLE_SIGNIN_DISABLED.
string? googleClientId = builder.Configuration["Authentication:Google:ClientId"];
if (!string.IsNullOrWhiteSpace(googleClientId))
{
    builder.Services.AddHttpClient(GoogleJwksSigningKeys.HttpClientName, client => client.Timeout = TimeSpan.FromSeconds(5));
    builder.Services.AddSingleton<IGoogleSigningKeys>(sp => new GoogleJwksSigningKeys(
        () => sp.GetRequiredService<IHttpClientFactory>().CreateClient(GoogleJwksSigningKeys.HttpClientName),
        sp.GetService<TimeProvider>() ?? TimeProvider.System));
    builder.Services.AddSingleton<IGoogleIdTokenVerifier>(sp => new GoogleIdTokenVerifier(googleClientId, sp.GetRequiredService<IGoogleSigningKeys>()));
}

builder.Services
    .AddAuthentication(JwtBearerAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, JwtBearerAuthenticationHandler>(
        JwtBearerAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, SellerResourceIsolationHandler>();
builder.Services.AddAuthorization();

// 3. Health Checks: readiness covers what the API needs to serve; object storage only when it is configured.
var healthChecks = builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"]);
if (objectStorageConfigured)
{
    builder.Services.AddHttpClient("MinioHealth", client => client.Timeout = TimeSpan.FromSeconds(3));
    healthChecks.AddCheck<MinioHealthCheck>("minio", tags: ["ready"]);
}

// 4. OpenAPI / Swagger Documentation
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "MECHWARE Core Commerce API",
        Version = "v1",
        Description = "Enterprise C# .NET 10 Web API for MECHWARE Industrial Commerce & Factory Automation",
        Contact = new OpenApiContact
        {
            Name = "MECHWARE Engineering",
            Email = "parvez.mechware@gmail.com",
            Url = new Uri("https://mechware.com.bd")
        }
    });

    var xmlFilename = $"{System.Reflection.Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = System.IO.Path.Combine(AppContext.BaseDirectory, xmlFilename);
    if (System.IO.File.Exists(xmlPath))
    {
        options.IncludeXmlComments(xmlPath);
    }

    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "Bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter JWT Bearer token to authorize requests: Bearer {token}"
    });

    options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecuritySchemeReference("Bearer", document),
            new List<string>()
        }
    });
});

// Budgets read once at startup.
int userPermitPerMinute = builder.Configuration.GetValue<int?>("RateLimiting:UserPermitPerMinute") ?? 600;
int anonymousPermitPerMinute = builder.Configuration.GetValue<int?>("RateLimiting:GlobalPermitPerMinute") ?? 100;
int webhookPermitPerMinute = builder.Configuration.GetValue<int?>("RateLimiting:WebhookPermitPerMinute") ?? 1000;
int ipCeilingPerMinute = builder.Configuration.GetValue<int?>("RateLimiting:IpCeilingPerMinute") ?? 3000;

// 5. Rate Limiting Middleware (Global 100 req/min + AuthStrictPolicy 5 req/min per IP per Brief §11.1)
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    options.AddPolicy("AuthStrictPolicy", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"auth:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = builder.Environment.IsProduction() ? 5 : 25,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));

    // Public storefront reads are cache-backed and get their own, higher ceiling (the global 100/min would throttle a PDP browse).
    options.AddPolicy("StorefrontRead", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"sf:{httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:StorefrontPermitPerMinute") ?? 1200,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));

    // Payment-provider webhooks get their own bucket per IP (threat model v2, P6): a provider delivers bursts from a few
    // addresses, and a 429 makes it retry later, which delays confirming paid orders. They never share the budget that
    // throttles browsers, and a flood of forged webhooks only exhausts its own bucket (each is refused on the signature).
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        httpContext.Request.Path.StartsWithSegments("/api/v1/storefront") || httpContext.Request.Path.StartsWithSegments("/api/v1/search")
        ? RateLimitPartition.GetNoLimiter("storefront")
        : httpContext.Request.Path.StartsWithSegments("/api/v1/payments/webhooks")
        ? RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "webhook:" + (httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown"),
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = webhookPermitPerMinute,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            })
        // A signed-in user has a budget of their own. A company's whole office usually shares one public IP (NAT), and
        // 100 requests a minute between 20 buyers would stall real work. Authentication runs before this limiter, so the
        // user id here comes from a verified token and a live session; a forged token counts as anonymous.
        : httpContext.User.Identity?.IsAuthenticated == true && httpContext.User.FindFirst("sub")?.Value is { } userId
        ? RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: "user:" + userId,
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = userPermitPerMinute,
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            })
        : RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = anonymousPermitPerMinute,
                // No queue: a request over the limit is told at once (429 + Retry-After) instead of hanging for up to a minute.
                QueueLimit = 0,
                Window = TimeSpan.FromMinutes(1)
            }));

    options.OnRejected = (context, ct) => TooManyRequests.WriteAsync(context.HttpContext, context.Lease, ct);
});

// The ceiling per IP, checked before authentication (which costs a database query for a bearer token): a flood is refused
// before it reaches the database, and many accounts behind one address cannot add up to more than this.
// Storefront reads and payment webhooks keep their own per-IP budgets (see above) and stay outside it.
var ipCeiling = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    httpContext.Request.Path.StartsWithSegments("/api/v1/storefront") || httpContext.Request.Path.StartsWithSegments("/api/v1/search")
    || httpContext.Request.Path.StartsWithSegments("/api/v1/payments/webhooks")
    ? RateLimitPartition.GetNoLimiter("own-budget")
    : RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            AutoReplenishment = true,
            PermitLimit = ipCeilingPerMinute,
            QueueLimit = 0,
            Window = TimeSpan.FromMinutes(1)
        }));

// 6. CORS Policy
builder.Services.AddCors(options =>
{
    options.AddPolicy("MechWareCors", policy =>
    {
        policy.WithOrigins(
                "http://localhost:3000",
                "http://localhost:3001",
                "https://mechware.com.bd",
                "https://console.mechware.com.bd")
            .AllowAnyHeader()
            .AllowAnyMethod()
            .AllowCredentials();
    });
});

builder.Services.AddControllers();

var app = builder.Build();

// Accounts an environment starts with live in the database. Outside Development the database is read at boot to make
// sure no account with a published demo credential exists, so the API needs the database to start there.
// The OpenAPI snapshot tool (dotnet swagger tofile) builds the host only to read the document: no database there.
if (Assembly.GetEntryAssembly()?.GetName().Name != "dotnet-swagger")
{
    var seedPolicy = app.Services.GetRequiredService<IdentitySeedPolicy>();
    if (seedPolicy.SeedDemoAccounts || bootstrapAdmin is not null)
    {
        using var seedScope = app.Services.CreateScope();
        await seedScope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync(seedPolicy, bootstrapAdmin);
    }

    // Not seeding demo accounts is not enough: a database that ever ran in Development still has them. Fail closed.
    if (!seedPolicy.SeedDemoAccounts)
    {
        using var guardScope = app.Services.CreateScope();
        await guardScope.ServiceProvider.GetRequiredService<DemoAccountGuard>()
            .EnsureNoPublishedCredentialAccountsAsync(app.Environment.EnvironmentName);
    }
}

if (app.Services.GetRequiredService<IdentitySeedPolicy>().SeedDemoAccounts)
{
    app.Logger.LogWarning("Demo accounts with PUBLISHED credentials are seeded because the environment is Development. They never exist in any other environment.");
}
if (bootstrapAdmin is not null)
{
    app.Logger.LogInformation("Bootstrap administrator {Email} is provisioned from configuration (two-factor enforced).", bootstrapAdmin.Email);
}

if (!app.Services.GetRequiredService<IdentitySeedPolicy>().SeedDemoAccounts)
{
    app.Logger.LogInformation("No demo accounts are seeded in the {Environment} environment; the identity store starts empty.", app.Environment.EnvironmentName);
}
if (!dataProtectionKeysEncrypted && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning("Data Protection keys are stored unencrypted in the database; set DataProtection__CertificatePath to encrypt them at rest.");
}

// Configure Middleware Pipeline
app.UseForwardedHeaders();
app.UseMiddleware<CorrelationIdMiddleware>();
app.UseMiddleware<SecurityHeadersMiddleware>();
app.UseMiddleware<GlobalExceptionHandlingMiddleware>();

app.UseSerilogRequestLogging(options =>
{
    options.MessageTemplate = "HTTP {RequestMethod} {RequestPath} responded {StatusCode} in {Elapsed:0.0000} ms";
});

app.UseCors("MechWareCors");
app.Use(async (context, next) =>
{
    using RateLimitLease lease = await ipCeiling.AcquireAsync(context, cancellationToken: context.RequestAborted);
    if (!lease.IsAcquired)
    {
        await TooManyRequests.WriteAsync(context, lease, context.RequestAborted);
        return;
    }
    await next(context);
});
// Authentication next, so the limiter can give each signed-in user their own budget (see AddRateLimiter).
app.UseAuthentication();
app.UseRateLimiter();
app.UseAuthorization();

// Swagger Documentation UI — Development only unless explicitly enabled (do not publish the API map in production).
if (app.Environment.IsDevelopment() || app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(c =>
    {
        c.SwaggerEndpoint("/swagger/v1/swagger.json", "MECHWARE API v1");
        c.RoutePrefix = "swagger";
    });
}

// Health Check Endpoints
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("live")
});

app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResponseWriter = async (context, report) =>
    {
        context.Response.ContentType = "application/json";
        var isDev = app.Environment.IsDevelopment();

        var result = System.Text.Json.JsonSerializer.Serialize(new
        {
            status = report.Status.ToString(),
            durationMs = Math.Round(report.TotalDuration.TotalMilliseconds, 2),
            checks = report.Entries.Select(e => new
            {
                name = e.Key,
                status = e.Value.Status.ToString(),
                description = isDev ? e.Value.Description : null,
                error = isDev ? e.Value.Exception?.Message : null,
                durationMs = Math.Round(e.Value.Duration.TotalMilliseconds, 2)
            })
        });
        await context.Response.WriteAsync(result);
    }
});

app.MapControllers();

app.Run();

// Required for WebApplicationFactory in integration tests
public partial class Program { }

/// <summary>A 429 in the API's usual error envelope, with Retry-After when the limiter knows it.</summary>
internal static class TooManyRequests
{
    public static async ValueTask WriteAsync(HttpContext context, RateLimitLease lease, CancellationToken ct)
    {
        context.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        if (lease.TryGetMetadata(MetadataName.RetryAfter, out TimeSpan retryAfter))
        {
            context.Response.Headers.RetryAfter = ((int)Math.Ceiling(retryAfter.TotalSeconds)).ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        // Same text as a business throttle's 429 (TooManyRequestsException), so callers cannot tell the limits apart.
        const string message = Mechware.Domain.Common.Exceptions.TooManyRequestsException.GenericMessage;
        string traceId = context.TraceIdentifier;
        await context.Response.WriteAsJsonAsync(new
        {
            type = "https://api.mechware.com.bd/errors/RATE_LIMITED",
            title = message,
            status = 429,
            detail = message,
            instance = context.Request.Path.Value,
            traceId,
            error = new { code = "RATE_LIMITED", message, details = Array.Empty<object>(), traceId }
        }, options: (System.Text.Json.JsonSerializerOptions?)null, contentType: "application/problem+json", cancellationToken: ct);
    }
}
