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
internal sealed class SearchIndexWorker(IServiceScopeFactory scopes, ILogger<SearchIndexWorker> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private bool _indexReady;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                if (!_indexReady)
                {
                    await scope.ServiceProvider.GetRequiredService<ISearchProvider>().EnsureIndexAsync(stoppingToken);
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
    }

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
