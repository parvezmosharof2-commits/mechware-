using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ShopVerse.Application.Common;

namespace ShopVerse.Infrastructure.Persistence.Outbox;

/// <summary>
/// Drains <c>outbox_messages</c> at-least-once. Rows are claimed with <c>FOR UPDATE SKIP LOCKED</c> so any
/// number of relay instances can run without double delivery of the same batch; consumers must be idempotent
/// (the message id is stable).
/// </summary>
public sealed class OutboxRelay
{
    public const int MaxRetries = 10;

    private readonly ShopVerseDbContext _db;
    private readonly IEnumerable<IOutboxPublisher> _publishers;
    private readonly ILogger<OutboxRelay> _logger;

    public OutboxRelay(ShopVerseDbContext db, IEnumerable<IOutboxPublisher> publishers, ILogger<OutboxRelay> logger)
    {
        _db = db;
        _publishers = publishers;
        _logger = logger;
    }

    /// <summary>Processes up to <paramref name="batchSize"/> messages; returns how many were delivered.</summary>
    public async Task<int> ProcessBatchAsync(int batchSize = 100, CancellationToken ct = default)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(ct);
        var batch = await _db.OutboxMessages
            .FromSql($"""
                SELECT * FROM outbox_messages
                WHERE processed_at IS NULL AND retry_count < {MaxRetries}
                ORDER BY occurred_at ASC
                LIMIT {batchSize}
                FOR UPDATE SKIP LOCKED
                """)
            .ToListAsync(ct);

        var delivered = 0;
        foreach (var message in batch)
        {
            var envelope = new OutboxEnvelope(message.Id, message.EventType, message.AggregateType, message.AggregateId,
                message.Payload, message.OccurredAtUtc);
            try
            {
                foreach (var publisher in _publishers) await publisher.PublishAsync(envelope, ct);
                message.ProcessedAtUtc = DateTime.UtcNow;
                message.Error = null;
                delivered++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                message.RetryCount++;
                message.Error = ex.Message.Length > 1000 ? ex.Message[..1000] : ex.Message;
                _logger.LogWarning(ex, "Outbox message {MessageId} ({EventType}) failed (attempt {Attempt})",
                    message.Id, message.EventType, message.RetryCount);
            }
        }

        await _db.SaveChangesAsync(ct);
        await tx.CommitAsync(ct);
        _db.ChangeTracker.Clear();
        return delivered;
    }
}
