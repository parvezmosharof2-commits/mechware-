using Mechware.Domain.Inventory;

namespace Mechware.Application.Inventory;

/// <summary>
/// A requested line. Deliberately carries <b>no price</b>: the service reads the authoritative price from the
/// catalogue (brief §8.1 — client-sent totals are ignored).
/// </summary>
public sealed record ReservationLine(Guid VariantId, int Quantity);

public sealed record ReserveStockRequest(Guid ReferenceId, IReadOnlyList<ReservationLine> Lines, TimeSpan? Ttl = null)
{
    public const int MaxLines = 50;
    public const int MaxQuantityPerLine = 10_000;
    /// <summary>
    /// The longest hold: an order waiting for its company's approval holds stock for up to 24 hours plus the 30 minutes to pay
    /// after approval (threat model v1, H1; ADR 0004). Ordinary checkouts hold for 30 minutes. Only server code sets a TTL.
    /// </summary>
    public static readonly TimeSpan MaxTtl = TimeSpan.FromHours(24) + TimeSpan.FromMinutes(30);
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

/// <summary>
/// Puts <paramref name="Quantity"/> units of one variant back on the shelf (E7.1). The idempotency key is
/// (<paramref name="ReferenceType"/>, <paramref name="ReferenceId"/>, <paramref name="Reason"/>, variant): a replay with the same
/// quantity changes nothing and returns the first result; a replay with another quantity is a conflict.
/// <para>Warehouse rule: with <see cref="OrderReference"/> the stock goes back to the warehouses the order's committed holds
/// took it from, and never more than they took minus what was already put back for that order; otherwise to
/// <paramref name="WarehouseId"/>, or to the variant's only active warehouse when none is given.</para>
/// <para><paramref name="AgainstOrderId"/> (F2.3a returns): stock that order deducted, put back under a reference of its own (one key per
/// return, so two partial returns of one variant never collide). The order's warehouse rule and cap apply exactly as for
/// <see cref="OrderReference"/>, counting everything put back against that order under any reference. Implied for an order reference.</para>
/// </summary>
public sealed record RestockRequest(
    Guid VariantId, int Quantity, RestockReason Reason, string ReferenceType, Guid ReferenceId,
    Guid? WarehouseId = null, Guid? ActorUserId = null, Guid? AgainstOrderId = null)
{
    public const int MaxQuantity = ReserveStockRequest.MaxQuantityPerLine;
    public const int MaxReferenceTypeLength = 40;
    /// <summary>The reference type of an order (its id is the reservation reference). Required for <see cref="RestockReason.OrderCancelled"/>.</summary>
    public const string OrderReference = "order";
}

public sealed record RestockedLine(Guid WarehouseId, int Quantity, int OnHandAfter);

/// <summary><paramref name="AlreadyApplied"/> is true for a replay; then <c>OnHandAfter</c> is the balance now.</summary>
public sealed record RestockResult(bool AlreadyApplied, IReadOnlyList<RestockedLine> Lines);

public interface IInventoryService
{
    /// <summary>
    /// Raises on-hand stock atomically with an append-only movement row and an <c>inventory.restocked</c> audit row; never
    /// changes reserved stock. Validation errors for bad input, <see cref="KeyNotFoundException"/> for an unknown variant or
    /// a missing stock record, a conflict for a different replay or for more than an order deducted. See <see cref="RestockRequest"/>.
    /// </summary>
    Task<RestockResult> RestockAsync(RestockRequest request, CancellationToken ct = default);

    /// <summary>Atomically reserves every line (15 minute TTL by default) or none.</summary>
    Task<ReserveStockResult> ReserveAsync(ReserveStockRequest request, CancellationToken ct = default);

    /// <summary>Converts active reservations for <paramref name="referenceId"/> into deductions (payment captured).</summary>
    Task<int> CommitAsync(Guid referenceId, CancellationToken ct = default);

    /// <summary>Releases active reservations for <paramref name="referenceId"/> (payment failed / cancelled).</summary>
    Task<int> CancelAsync(Guid referenceId, CancellationToken ct = default);

    /// <summary>Releases reservations past their TTL in a set-based, lock-skipping sweep. Returns released count.</summary>
    Task<int> ReleaseExpiredAsync(DateTime nowUtc, int batchSize = 500, CancellationToken ct = default);
}
