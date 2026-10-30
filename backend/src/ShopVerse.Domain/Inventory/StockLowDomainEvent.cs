using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Inventory;

/// <summary>Raised (via the outbox) when available stock drops to or below the low-stock threshold.</summary>
public sealed record StockLowDomainEvent(
    Guid InventoryItemId, Guid VariantId, Guid WarehouseId, int Available, int Threshold) : DomainEventBase;
