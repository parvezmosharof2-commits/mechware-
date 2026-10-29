using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Inventory;

/// <summary>
/// Stock balance of one variant in one warehouse. Invariants (ADR 0004):
/// <c>Available = OnHand - Reserved</c>, <c>Reserved ≥ 0</c>, <c>OnHand ≥ Reserved</c>.
/// </summary>
[NotAudited]
public sealed class InventoryItem : AggregateRoot<Guid>
{
    public Guid VariantId { get; private set; }
    public Guid WarehouseId { get; private set; }
    public int OnHand { get; private set; }
    public int Reserved { get; private set; }
    public int LowStockThreshold { get; private set; } = 5;

    public int Available => OnHand - Reserved;

    private InventoryItem() { }

    public InventoryItem(Guid id, Guid variantId, Guid warehouseId, int onHand, int lowStockThreshold = 5) : base(id)
    {
        if (onHand < 0) throw new ValidationException("onHand", "On-hand stock cannot be negative.");
        if (lowStockThreshold < 0) throw new ValidationException("lowStockThreshold", "Threshold cannot be negative.");
        VariantId = variantId;
        WarehouseId = warehouseId;
        OnHand = onHand;
        LowStockThreshold = lowStockThreshold;
    }

    public bool CanReserve(int quantity) => quantity > 0 && Available >= quantity;

    /// <summary>Places a hold. Returns false (state unchanged) when stock is insufficient.</summary>
    public bool TryReserve(int quantity)
    {
        if (quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
        if (Available < quantity) return false;

        var before = Available;
        Reserved += quantity;
        Touch();
        RaiseLowStockIfCrossed(before);
        return true;
    }

    /// <summary>Releases a hold without consuming stock (cancel / expiry).</summary>
    public void Release(int quantity)
    {
        if (quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
        if (quantity > Reserved) throw new ValidationException("quantity", "Cannot release more than is reserved.");
        Reserved -= quantity;
        Touch();
    }

    /// <summary>Converts a hold into a deduction after payment capture.</summary>
    public void Commit(int quantity)
    {
        if (quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
        if (quantity > Reserved) throw new ValidationException("quantity", "Cannot commit more than is reserved.");
        var before = Available;
        Reserved -= quantity;
        OnHand -= quantity;
        Touch();
        RaiseLowStockIfCrossed(before);
    }

    /// <summary>Applies a physical stock correction (receipt, cycle count). Never below the reserved quantity.</summary>
    public void AdjustOnHand(int newOnHand)
    {
        if (newOnHand < Reserved)
            throw new ValidationException("onHand", "On-hand stock cannot drop below the reserved quantity.");
        var before = Available;
        OnHand = newOnHand;
        Touch();
        RaiseLowStockIfCrossed(before);
    }

    public void SetThreshold(int threshold)
    {
        if (threshold < 0) throw new ValidationException("lowStockThreshold", "Threshold cannot be negative.");
        LowStockThreshold = threshold;
        Touch();
    }

    private void RaiseLowStockIfCrossed(int availableBefore)
    {
        // Fire once on the downward crossing, not on every change below the line.
        if (availableBefore > LowStockThreshold && Available <= LowStockThreshold)
        {
            AddDomainEvent(new StockLowDomainEvent(Id, VariantId, WarehouseId, Available, LowStockThreshold));
        }
    }

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}
