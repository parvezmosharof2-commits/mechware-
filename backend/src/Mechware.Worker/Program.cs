using Microsoft.EntityFrameworkCore;
using Mechware.Infrastructure;
using Mechware.Infrastructure.Persistence;
using Mechware.Infrastructure.Persistence.Outbox;
using Mechware.Infrastructure.Search;
using Mechware.Infrastructure.Seed;
using StackExchange.Redis;

// Background worker host: stock-reservation expiry sweep + transactional-outbox relay (Phase 4).
// `dotnet run --project src/Mechware.Worker -- seed-catalog [--seed N] [--media-root DIR] [--report FILE] [--no-media]`
// applies migrations and loads the deterministic demo catalogue, then exits. `-- migrate` only applies migrations.
if (args.Length > 0 && args[0] == "seed-catalog")
{
    return await RunSeedAsync(args);
}

var builder = Host.CreateApplicationBuilder(args);

// `-- migrate` is the deploy step: applies pending migrations and exits 0, or prints the error and exits 1. Run it once per
// release before the new API/Worker start (the API never migrates). Concurrent runs are safe (see MechwareDatabase.MigrateAsync).
if (args.Length > 0 && args[0] == "migrate")
{
    return await MechwareDatabase.MigrateAsync(MechwareDatabase.ConnectionString(builder.Configuration), Console.Out, Console.Error);
}

// `-- rebuild-search` asks the running indexer for a full rebuild (fresh index + swap) and exits.
if (args.Length > 0 && args[0] == "rebuild-search")
{
    using var redis = await ConnectionMultiplexer.ConnectAsync(RedisConnection(builder.Configuration));
    await redis.GetDatabase().StringSetAsync(SearchIndexer.RebuildRequestKey, DateTime.UtcNow.ToString("O"));
    Console.WriteLine("Search rebuild requested; the running Worker performs it on its next tick.");
    return 0;
}

builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
{
    var options = ConfigurationOptions.Parse(RedisConnection(builder.Configuration));
    options.AbortOnConnectFail = false;
    return ConnectionMultiplexer.Connect(options);
});

builder.Services.AddCatalogServices();
builder.Services.AddDbContext<MechwareDbContext>((sp, options) =>
{
    options.UseNpgsql(MechwareDatabase.ConnectionString(builder.Configuration)).AddInterceptors(sp.GetRequiredService<DomainEventOutboxInterceptor>());
});
builder.Services.AddCatalogWorkers();

// Search index (C6). Outside Development and Testing the engine key must be configured (Search__ApiKey).
var search = builder.Configuration.GetSection(SearchOptions.SectionName).Get<SearchOptions>() ?? new SearchOptions();
builder.Services.AddSearch(search, requireRealSetup: !(builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")));
builder.Services.AddSearchIndexing();

// Email (F3a). Outside Development and Testing Email__Host and Email__FromAddress must be configured.
builder.Services.AddNotifications(
    builder.Configuration.GetSection(Mechware.Infrastructure.Notifications.EmailOptions.SectionName).Get<Mechware.Infrastructure.Notifications.EmailOptions>() ?? new(),
    requireRealSetup: !(builder.Environment.IsDevelopment() || builder.Environment.IsEnvironment("Testing")),
    storefrontBaseUrl: builder.Configuration["Storefront:BaseUrl"]);

await builder.Build().RunAsync();
return 0;

static string RedisConnection(IConfiguration configuration) =>
    configuration.GetConnectionString("Redis") ?? configuration["Redis:ConnectionString"] ?? "localhost:6379";

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

    string connectionString = MechwareDatabase.ConnectionString(configuration);
    if (await MechwareDatabase.MigrateAsync(connectionString, Console.Out, Console.Error) != 0) return 1;

    var dbOptions = new DbContextOptionsBuilder<MechwareDbContext>()
        .UseNpgsql(connectionString)
        .AddInterceptors(new DomainEventOutboxInterceptor())
        .Options;
    await using var db = new MechwareDbContext(dbOptions);

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
