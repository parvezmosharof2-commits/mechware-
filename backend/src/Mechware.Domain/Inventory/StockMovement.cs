using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Inventory;

/// <summary>Why stock went back on the shelf. Starts at 1 so a missing value (0) is never a valid reason.</summary>
public enum RestockReason { OrderCancelled = 1, ReturnRestocked = 2, Correction = 3, StockReceived = 4 }

/// <summary>
/// One append-only line of the stock ledger (E7.1): <see cref="Delta"/> units of one warehouse balance, for a reason and a
/// business reference. A database trigger forbids UPDATE, DELETE and TRUNCATE; a unique index on
/// (reference type, reference id, reason, balance) makes a replayed restock impossible to apply twice. The explicit
/// <c>inventory.restocked</c> audit row records the same facts, so the interceptor does not copy this row again.
/// </summary>
[NotAudited]
public sealed class StockMovement : Entity<Guid>
{
    public Guid InventoryItemId { get; private set; }
    public int Delta { get; private set; }
    public RestockReason Reason { get; private set; }
    public string ReferenceType { get; private set; } = string.Empty;
    public Guid ReferenceId { get; private set; }
    /// <summary>Null when the system did it (e.g. a sweep).</summary>
    public Guid? ActorId { get; private set; }

    private StockMovement() { }

    public StockMovement(Guid id, Guid inventoryItemId, int delta, RestockReason reason, string referenceType, Guid referenceId,
        Guid? actorId, DateTime nowUtc) : base(id)
    {
        if (delta == 0) throw new ValidationException("quantity", "A stock movement moves at least one unit.");
        if (!Enum.IsDefined(reason)) throw new ValidationException("reason", "A known reason is required.");
        if (string.IsNullOrWhiteSpace(referenceType) || referenceId == Guid.Empty)
            throw new ValidationException("reference", "A reference is required.");
        InventoryItemId = inventoryItemId;
        Delta = delta;
        Reason = reason;
        ReferenceType = referenceType;
        ReferenceId = referenceId;
        ActorId = actorId;
        CreatedAtUtc = nowUtc;
    }
}
