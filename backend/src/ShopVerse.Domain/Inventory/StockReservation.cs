using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Inventory;

public enum ReservationStatus { Active = 1, Fulfilled = 2, Expired = 3, Cancelled = 4 }

/// <summary>A time-boxed (15 minute) stock hold tied to a cart / checkout / order reference.</summary>
[NotAudited]
public sealed class StockReservation : Entity<Guid>
{
    public static readonly TimeSpan DefaultTtl = TimeSpan.FromMinutes(15);

    public Guid VariantId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public Guid ReferenceId { get; private set; }
    public int Quantity { get; private set; }
    public long LockedUnitPriceMinor { get; private set; }
    public ReservationStatus Status { get; private set; } = ReservationStatus.Active;
    public DateTime ExpiresAtUtc { get; private set; }

    private StockReservation() { }

    public StockReservation(Guid id, Guid variantId, Guid warehouseId, Guid referenceId, int quantity,
        long lockedUnitPriceMinor, DateTime nowUtc, TimeSpan? ttl = null) : base(id)
    {
        if (quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
        if (lockedUnitPriceMinor <= 0) throw new ValidationException("lockedUnitPrice", "Locked price must be positive.");
        VariantId = variantId;
        WarehouseId = warehouseId;
        ReferenceId = referenceId;
        Quantity = quantity;
        LockedUnitPriceMinor = lockedUnitPriceMinor;
        ExpiresAtUtc = nowUtc + (ttl ?? DefaultTtl);
    }

    public bool IsExpired(DateTime nowUtc) => Status == ReservationStatus.Active && ExpiresAtUtc <= nowUtc;

    public void MarkFulfilled() => Transition(ReservationStatus.Fulfilled);
    public void MarkCancelled() => Transition(ReservationStatus.Cancelled);
    public void MarkExpired() => Transition(ReservationStatus.Expired);

    private void Transition(ReservationStatus to)
    {
        if (Status != ReservationStatus.Active)
            throw new ValidationException("status", $"Reservation is already {Status}.");
        Status = to;
        UpdatedAtUtc = DateTime.UtcNow;
    }
}
