using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ShopVerse.Application.Audit;

namespace ShopVerse.Infrastructure.Workers;

/// <summary>
/// Seals new audit rows into the hash chain once a minute, and logs the chain head. The log line is the anchor kept outside
/// the database: rewriting history would also mean rewriting every head already shipped to the log store.
/// </summary>
public sealed class AuditChainWorker(IServiceScopeFactory scopes, ILogger<AuditChainWorker> logger) : BackgroundService
{
    public static readonly TimeSpan Interval = TimeSpan.FromSeconds(60);
    /// <summary>Rows younger than this may still belong to a transaction that has not committed; they wait for the next run.</summary>
    public static readonly TimeSpan Settle = TimeSpan.FromSeconds(30);
    private const int Batch = 5000;
    /// <summary>A full recomputation reads the whole log, so it runs this often (and on request via the admin endpoint).</summary>
    public static readonly TimeSpan FullVerifyEvery = TimeSpan.FromHours(24);
    // The first full check comes at a random point in the first hour, so instances started together do not all scan at once.
    private DateTimeOffset _lastFullVerify = DateTimeOffset.UtcNow - FullVerifyEvery + TimeSpan.FromMinutes(Random.Shared.Next(1, 60));
    private (long Position, string? Hash)? _lastHead;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(Interval);
        do
        {
            try
            {
                await using var scope = scopes.CreateAsyncScope();
                var chain = scope.ServiceProvider.GetRequiredService<IAuditChain>();
                int sealedNow;
                do { sealedNow = await chain.SealAsync(Batch, Settle, stoppingToken); } while (sealedNow == Batch && !stoppingToken.IsCancellationRequested);

                var (position, hash) = await chain.HeadAsync(stoppingToken);
                logger.LogInformation("Audit chain head {Position} {Hash}", position, hash);

                // The chain only ever grows, and a sealed position never changes its hash. A shorter chain or a different
                // hash at a position already seen means rows were removed or rewritten at the end, which the chain alone
                // cannot show.
                if (_lastHead is { } seen && HeadWentBack(seen, (position, hash)))
                    logger.LogCritical("Audit chain head went from {SeenPosition} {SeenHash} to {Position} {Hash}: the end of the audit log was altered",
                        seen.Position, seen.Hash, position, hash);
                _lastHead = (position, hash);

                if (DateTimeOffset.UtcNow - _lastFullVerify >= FullVerifyEvery)
                {
                    var status = await chain.VerifyAsync(stoppingToken);
                    _lastFullVerify = DateTimeOffset.UtcNow;
                    if (!status.Intact)
                        logger.LogCritical("Audit chain BROKEN at position {Position} of {Sealed}: the audit log was altered", status.FirstBrokenPosition, status.Sealed);
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogError(ex, "Audit chain sealing failed");
            }
        } while (await WaitAsync(timer, stoppingToken));
    }

    /// <summary>True when the head is behind one already seen, or the same position now carries a different hash.</summary>
    public static bool HeadWentBack((long Position, string? Hash) seen, (long Position, string? Hash) now) =>
        now.Position < seen.Position || (now.Position == seen.Position && now.Hash != seen.Hash);

    private static async Task<bool> WaitAsync(PeriodicTimer timer, CancellationToken ct)
    {
        try { return await timer.WaitForNextTickAsync(ct); }
        catch (OperationCanceledException) { return false; }
    }
}
