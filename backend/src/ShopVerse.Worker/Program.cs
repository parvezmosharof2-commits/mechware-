using Microsoft.EntityFrameworkCore;
using ShopVerse.Infrastructure;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.Infrastructure.Persistence.Outbox;
using StackExchange.Redis;

// Background worker host: stock-reservation expiry sweep + transactional-outbox relay (Phase 4).
var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var conn = builder.Configuration.GetConnectionString("Redis")
        ?? builder.Configuration["Redis:ConnectionString"]
        ?? "localhost:6379";
    var options = ConfigurationOptions.Parse(conn);
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddCatalogServices();
builder.Services.AddDbContext<ShopVerseDbContext>((sp, options) =>
{
    var conn = builder.Configuration.GetConnectionString("DefaultConnection")
        ?? builder.Configuration["Database:ConnectionString"]
        ?? "Host=localhost;Port=5432;Database=shopverse;Username=shopverse;Password=shopverse_secret";
    options.UseNpgsql(conn).AddInterceptors(sp.GetRequiredService<DomainEventOutboxInterceptor>());
});
builder.Services.AddCatalogWorkers();

await builder.Build().RunAsync();
