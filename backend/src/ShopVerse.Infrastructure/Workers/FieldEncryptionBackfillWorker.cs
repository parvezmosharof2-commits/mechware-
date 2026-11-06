using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.Infrastructure.Security;

namespace ShopVerse.Infrastructure.Workers;

/// <summary>
/// Once at start-up: encrypts business identifiers written before encryption existed and fills their BIN fingerprints.
/// Rows are read through the converters (which pass old plain values through) and saved again (which encrypts them).
/// </summary>
public sealed class FieldEncryptionBackfillWorker(IServiceScopeFactory scopes, ILogger<FieldEncryptionBackfillWorker> logger) : BackgroundService
{
    private const int Batch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            int done = await RunAsync(scope.ServiceProvider.GetRequiredService<ShopVerseDbContext>(), stoppingToken);
            if (done > 0) logger.LogInformation("Encrypted business identifiers of {Count} companies written before encryption", done);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Encrypting older business identifiers failed; they stay readable and are retried at next start");
        }
    }

    public static async Task<int> RunAsync(ShopVerseDbContext db, CancellationToken ct)
    {
        string like = FieldProtector.Prefix + "%";
        int total = 0;
        while (true)
        {
            var ids = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM companies
                 WHERE bin NOT LIKE {like} OR trade_licence_no NOT LIKE {like} OR (tin IS NOT NULL AND tin NOT LIKE {like})
                    OR bin_index IS NULL
                 LIMIT {Batch}
                """).ToListAsync(ct);
            if (ids.Count == 0) return total;

            var companies = await db.Companies.Where(c => ids.Contains(c.Id)).ToListAsync(ct);
            foreach (var company in companies)
            {
                var entry = db.Entry(company);
                entry.Property(c => c.Bin).IsModified = true;
                entry.Property(c => c.TradeLicenceNo).IsModified = true;
                entry.Property(c => c.Tin).IsModified = true;
            }
            await db.SaveChangesAsync(ct);
            db.ChangeTracker.Clear();
            total += companies.Count;
        }
    }
}
