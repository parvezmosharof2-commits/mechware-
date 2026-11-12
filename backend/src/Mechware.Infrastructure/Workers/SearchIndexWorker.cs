using Mechware.Application.Search;
using Mechware.Infrastructure.Search;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Mechware.Infrastructure.Workers;

/// <summary>
/// Feeds the search index from the outbox queue every couple of seconds, and runs a full rebuild when one is requested.
/// Host it once (Mechware.Worker); an outage of the engine only delays indexing (see <see cref="SearchIndexer"/>).
/// </summary>
internal sealed class SearchIndexWorker(IServiceScopeFactory scopes, StackExchange.Redis.IConnectionMultiplexer redis, ILogger<SearchIndexWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private bool _indexReady;
    private readonly SearchIndexLease _lease = new(redis, $"{Environment.MachineName}:{Environment.ProcessId}:{Guid.NewGuid():N}");

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                if (!await _lease.TryAcquireOrRenewAsync()) continue; // another instance is the consumer
                await using var scope = scopes.CreateAsyncScope();
                if (!_indexReady)
                {
                    var provider = scope.ServiceProvider.GetRequiredService<ISearchProvider>();
                    await provider.EnsureIndexAsync(stoppingToken);
                    // Owner/admin-editable synonym list comes later; until then the built-in default is applied on every start.
                    await provider.UpdateSynonymsAsync(SearchSynonyms.Default, stoppingToken);
                    _indexReady = true;
                }
                var applied = await scope.ServiceProvider.GetRequiredService<SearchIndexer>().RunOnceAsync(stoppingToken);
                if (applied > 0) logger.LogInformation("Applied {Count} search-sync messages", applied);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Search indexing failed; will retry");
            }
        } while (await WaitAsync(timer, stoppingToken));

        try { await _lease.ReleaseAsync(); }
        catch (Exception ex) { logger.LogWarning(ex, "Could not release the search indexer lease; it lapses on its own"); }
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
