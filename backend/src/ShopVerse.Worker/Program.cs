using Microsoft.EntityFrameworkCore;
using ShopVerse.Infrastructure;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.Infrastructure.Persistence.Outbox;
using ShopVerse.Infrastructure.Seed;
using StackExchange.Redis;

// Background worker host: stock-reservation expiry sweep + transactional-outbox relay (Phase 4).
// `dotnet run --project src/ShopVerse.Worker -- seed-catalog [--seed N] [--media-root DIR] [--report FILE] [--no-media]`
// applies migrations and loads the deterministic demo catalogue, then exits.
if (args.Length > 0 && args[0] == "seed-catalog")
{
    return await RunSeedAsync(args);
}

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
    options.UseNpgsql(ConnectionString(builder.Configuration)).AddInterceptors(sp.GetRequiredService<DomainEventOutboxInterceptor>());
});
builder.Services.AddCatalogWorkers();

await builder.Build().RunAsync();
return 0;

static string ConnectionString(IConfiguration configuration) =>
    configuration.GetConnectionString("DefaultConnection")
    ?? configuration["Database:ConnectionString"]
    ?? "Host=localhost;Port=5432;Database=shopverse;Username=shopverse;Password=shopverse_secret";

static async Task<int> RunSeedAsync(string[] args)
{
    var configuration = new ConfigurationBuilder().AddEnvironmentVariables().AddJsonFile("appsettings.json", optional: true).Build();
    var options = new SeedOptions();
    string? reportPath = null;
    for (var i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--seed": options.RandomSeed = int.Parse(args[++i]); break;
            case "--media-root": options.MediaRoot = args[++i]; break;
            case "--media-url": options.MediaBaseUrl = args[++i]; break;
            case "--report": reportPath = args[++i]; break;
            case "--no-media": options.WriteMediaFiles = false; break;
            default: Console.Error.WriteLine($"Unknown option {args[i]}"); return 2;
        }
    }

    var dbOptions = new DbContextOptionsBuilder<ShopVerseDbContext>()
        .UseNpgsql(ConnectionString(configuration))
        .AddInterceptors(new DomainEventOutboxInterceptor())
        .Options;
    await using var db = new ShopVerseDbContext(dbOptions);

    Console.WriteLine("Applying migrations…");
    await db.Database.MigrateAsync();

    Console.WriteLine($"Seeding catalogue (seed {options.RandomSeed})…");
    var report = await CatalogSeeder.SeedAsync(db, options);
    var markdown = SeedReportWriter.ToMarkdown(report, options);
    Console.WriteLine(markdown);
    if (reportPath is not null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
        await File.WriteAllTextAsync(reportPath, markdown);
        Console.WriteLine($"Report written to {reportPath}");
    }

    return 0;
}
