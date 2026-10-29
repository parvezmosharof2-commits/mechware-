namespace ShopVerse.Application.Inventory;

/// <summary>
/// A requested line. Deliberately carries <b>no price</b>: the service reads the authoritative price from the
/// catalogue (brief §8.1 — client-sent totals are ignored).
/// </summary>
public sealed record ReservationLine(Guid VariantId, int Quantity);

public sealed record ReserveStockRequest(Guid ReferenceId, IReadOnlyList<ReservationLine> Lines, TimeSpan? Ttl = null)
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 10_000;
    public static readonly TimeSpan MaxTtl = TimeSpan.FromMinutes(30);
}

public sealed record InsufficientStockLine(Guid VariantId, int Requested, int Available);

/// <summary>Outcome of a reservation attempt. All-or-nothing: on failure no stock is held.</summary>
public sealed record ReserveStockResult(
    bool Succeeded,
    IReadOnlyList<Guid> ReservationIds,
    DateTime? ExpiresAtUtc,
    IReadOnlyList<InsufficientStockLine> Shortfalls,
    IReadOnlyList<Guid>? UnavailableVariants = null)
{
    public static ReserveStockResult Ok(IReadOnlyList<Guid> ids, DateTime expiresAtUtc) => new(true, ids, expiresAtUtc, []);
    public static ReserveStockResult Insufficient(IReadOnlyList<InsufficientStockLine> lines) => new(false, [], null, lines);
    public static ReserveStockResult Busy() => new(false, [], null, []);

    /// <summary>Unknown, inactive, archived or deleted variants cannot be reserved.</summary>
    public static ReserveStockResult Unavailable(IReadOnlyList<Guid> variants) => new(false, [], null, [], variants);
}

public interface IInventoryService
{
    /// <summary>Atomically reserves every line (15 minute TTL by default) or none.</summary>
    Task<ReserveStockResult> ReserveAsync(ReserveStockRequest request, CancellationToken ct = default);

    /// <summary>Converts active reservations for <paramref name="referenceId"/> into deductions (payment captured).</summary>
    Task<int> CommitAsync(Guid referenceId, CancellationToken ct = default);

    /// <summary>Releases active reservations for <paramref name="referenceId"/> (payment failed / cancelled).</summary>
    Task<int> CancelAsync(Guid referenceId, CancellationToken ct = default);

    /// <summary>Releases reservations past their TTL in a set-based, lock-skipping sweep. Returns released count.</summary>
    Task<int> ReleaseExpiredAsync(DateTime nowUtc, int batchSize = 500, CancellationToken ct = default);
}
