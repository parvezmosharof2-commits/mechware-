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
using ShopVerse.Api.Authentication;
using ShopVerse.Api.Authorization;
using ShopVerse.Api.Configuration;
using ShopVerse.Api.Health;
using ShopVerse.Api.Middleware;
using ShopVerse.Api.Services;
using ShopVerse.Application.Identity;
using ShopVerse.Infrastructure;
using ShopVerse.Infrastructure.Identity;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.Infrastructure.Persistence.Outbox;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);

// 1. Serilog Structured Logging
Log.Logger = new LoggerConfiguration()
    .ReadFrom.Configuration(builder.Configuration)
    .Enrich.FromLogContext()
    .CreateLogger();

builder.Host.UseSerilog();

// 2. Strongly Typed Configuration with Startup Validation
builder.Services.AddOptions<DatabaseOptions>()
    .BindConfiguration(DatabaseOptions.SectionName)
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

builder.Services.AddOptions<StorageOptions>()
    .BindConfiguration(StorageOptions.SectionName)
    .ValidateDataAnnotations()
    .ValidateOnStart();

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
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;

    if (builder.Environment.IsDevelopment() || builder.Environment.EnvironmentName == "Testing")
    {
        options.KnownIPNetworks.Clear();
        options.KnownProxies.Clear();
    }
});

// 2.3 Phase 3 Identity, Persistence & RBAC Registration
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<ICurrentUserService, HttpContextCurrentUserService>();
builder.Services.AddScoped<AuditLogSaveChangesInterceptor>();

builder.Services.AddDbContext<ShopVerseDbContext>((sp, options) =>
{
    var dbConn = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? builder.Configuration.GetSection(DatabaseOptions.SectionName)["ConnectionString"]
        ?? "Host=localhost;Port=5432;Database=mechware_db;Username=mechware;Password=mechware_dev_password";

    options.UseNpgsql(dbConn)
        .AddInterceptors(
            sp.GetRequiredService<AuditLogSaveChangesInterceptor>(),
            sp.GetRequiredService<DomainEventOutboxInterceptor>());
});

// 2.4 Phase 4 Catalog, Inventory, Outbox and storefront cache (hosted workers live in ShopVerse.Worker)
builder.Services.AddCatalogServices();

builder.Services.AddSingleton<IPasswordHasherService, PasswordHasherService>();
builder.Services.AddSingleton<ITotpService, TotpService>();
builder.Services.AddSingleton<ISmsProvider, LoggingSmsProvider>();
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
// First administrator for environments without demo accounts; secrets come from deployment configuration only.
var bootstrapAdmin = BootstrapAdminSettings.FromValues(
    builder.Configuration["Identity:BootstrapAdmin:Email"],
    builder.Configuration["Identity:BootstrapAdmin:FullName"],
    builder.Configuration["Identity:BootstrapAdmin:Phone"],
    builder.Configuration["Identity:BootstrapAdmin:Password"],
    builder.Configuration["Identity:BootstrapAdmin:TotpSecret"]);
builder.Services.AddScoped<IdentitySeeder>();
builder.Services.AddScoped<IIdentityService, IdentityService>();

builder.Services
    .AddAuthentication(JwtBearerAuthenticationHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, JwtBearerAuthenticationHandler>(
        JwtBearerAuthenticationHandler.SchemeName,
        _ => { });

builder.Services.AddSingleton<IAuthorizationPolicyProvider, PermissionAuthorizationPolicyProvider>();
builder.Services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();
builder.Services.AddSingleton<IAuthorizationHandler, SellerResourceIsolationHandler>();
builder.Services.AddAuthorization();

// 3. Health Checks
builder.Services.AddHttpClient("MinioHealth", client =>
{
    client.Timeout = TimeSpan.FromSeconds(3);
});

builder.Services.AddHealthChecks()
    .AddCheck("self", () => HealthCheckResult.Healthy(), tags: ["live"])
    .AddCheck<PostgreSqlHealthCheck>("postgresql", tags: ["ready"])
    .AddCheck<RedisHealthCheck>("redis", tags: ["ready"])
    .AddCheck<MinioHealthCheck>("minio", tags: ["ready"]);

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

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        httpContext.Request.Path.StartsWithSegments("/api/v1/storefront")
        ? RateLimitPartition.GetNoLimiter("storefront")
        : RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                AutoReplenishment = true,
                PermitLimit = builder.Configuration.GetValue<int?>("RateLimiting:GlobalPermitPerMinute") ?? 100,
                QueueLimit = 10,
                Window = TimeSpan.FromMinutes(1)
            }));
});

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

// Accounts an environment starts with live in the database. Nothing is touched when there is nothing to seed, so an
// environment without demo accounts or a bootstrap administrator starts without needing the database at boot.
{
    var seedPolicy = app.Services.GetRequiredService<IdentitySeedPolicy>();
    if (seedPolicy.SeedDemoAccounts || bootstrapAdmin is not null)
    {
        using var seedScope = app.Services.CreateScope();
        await seedScope.ServiceProvider.GetRequiredService<IdentitySeeder>().SeedAsync(seedPolicy, bootstrapAdmin);
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
app.UseRateLimiter();
app.UseAuthentication();
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
