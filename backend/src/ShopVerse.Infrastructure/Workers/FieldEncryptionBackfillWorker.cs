using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopVerse.Infrastructure.Persistence;
using ShopVerse.Infrastructure.Security;

namespace ShopVerse.Infrastructure.Workers;

/// <summary>
/// Once at start-up: brings every company's business identifiers up to the current key. That covers values written before
/// encryption existed, values in the first format (no key id), values under a previous key after a rotation, and missing BIN
/// fingerprints. Rows are read through the converters (which read all of those) and saved again (which writes the BIN, its
/// fingerprint and the other identifiers under the current key). When it reports nothing left, previous keys can be retired.
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
            if (done > 0) logger.LogInformation("Re-encrypted business identifiers of {Count} companies under the current key", done);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Encrypting older business identifiers failed; they stay readable and are retried at next start");
        }
    }

    public static async Task<int> RunAsync(ShopVerseDbContext db, CancellationToken ct)
    {
        string like = FieldEncryption.Current.CurrentPrefix + "%";
        int total = 0;
        var done = new HashSet<Guid>();
        while (true)
        {
            var ids = await db.Database.SqlQuery<Guid>($"""
                SELECT id AS "Value" FROM companies
                 WHERE bin NOT LIKE {like} OR trade_licence_no NOT LIKE {like} OR (tin IS NOT NULL AND tin NOT LIKE {like})
                    OR bin_index IS NULL
                 LIMIT {Batch}
                """).ToListAsync(ct);
            if (ids.Count == 0) return total;
            // A row saved once and still selected did not reach the current key; selecting it again would loop for ever.
            var stuck = ids.Where(done.Contains).ToList();
            if (stuck.Count > 0)
                throw new InvalidOperationException(
                    $"{stuck.Count} companies were re-encrypted but still are not under the current key (first: {stuck[0]}); stopping instead of retrying them for ever.");
            done.UnionWith(ids);

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
