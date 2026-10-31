using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Domain.Cart;

/// <summary>One line of a cart. The price is only what the buyer last saw; checkout always re-prices on the server.</summary>
public sealed record CartItem(Guid VariantId, int Quantity, long SeenUnitNetMinor);

public sealed record Cart(IReadOnlyList<CartItem> Items, int Version = 0)
{
    public static Cart Empty { get; } = new([]);
}

/// <summary>What the catalog and inventory say about a variant right now. Supplied by the caller; the engine does no I/O.</summary>
/// <param name="Sellable">Active, published and visible to this buyer.</param>
/// <param name="Available">Units that can still be reserved (on hand minus reserved).</param>
public sealed record VariantFacts(
    Guid VariantId, bool Sellable, long UnitNetMinor, int Available, int MinOrderQty = 1, int PackSize = 1);

public enum CartNoticeKind
{
    PriceChanged = 1,
    StockReduced = 2,
    OutOfStock = 3,
    Unavailable = 4,
    BelowMinimumOrder = 5,
    QuantityAdjustedToPack = 6,
    QuantityCapped = 7
}

public sealed record CartNotice(CartNoticeKind Kind, Guid VariantId, long? Was = null, long? Now = null);

public sealed record CartChange(Cart Cart, IReadOnlyList<CartNotice> Notices);

/// <summary>
/// Pure cart rules (Phase 8): add, set, remove, merge a guest cart into a signed-in one, and re-validate against the
/// catalog so price and stock drift is reported instead of silently changing what the buyer pays.
/// </summary>
public static class CartEngine
{
    public const int MaxLines = 100;
    public const int MaxQuantityPerLine = 10_000;

    public static Cart Add(Cart cart, VariantFacts facts, int quantity)
    {
        if (quantity <= 0) throw new ValidationException("quantity", "Quantity must be positive.");
        if (!facts.Sellable) throw new ConflictException("VARIANT_UNAVAILABLE", "This item is not available.");

        var existing = cart.Items.FirstOrDefault(i => i.VariantId == facts.VariantId);
        var total = (long)(existing?.Quantity ?? 0) + quantity;
        return Set(cart, facts, (int)Math.Min(total, int.MaxValue));
    }

    /// <summary>Sets a line to exactly <paramref name="quantity"/>; zero removes it.</summary>
    public static Cart Set(Cart cart, VariantFacts facts, int quantity)
    {
        if (quantity < 0) throw new ValidationException("quantity", "Quantity cannot be negative.");
        if (quantity == 0) return Remove(cart, facts.VariantId);
        if (!facts.Sellable) throw new ConflictException("VARIANT_UNAVAILABLE", "This item is not available.");

        var min = Math.Max(1, facts.MinOrderQty);
        var pack = Math.Max(1, facts.PackSize);
        if (quantity < min) throw new ValidationException("quantity", $"The minimum order quantity is {min}.");
        if (quantity % pack != 0) throw new ValidationException("quantity", $"This item is sold in packs of {pack}.");
        if (quantity > MaxQuantityPerLine) throw new ValidationException("quantity", $"At most {MaxQuantityPerLine} per line.");
        if (quantity > facts.Available) throw new ConflictException("INSUFFICIENT_STOCK", $"Only {facts.Available} available.");

        var items = cart.Items.ToList();
        var index = items.FindIndex(i => i.VariantId == facts.VariantId);
        var line = new CartItem(facts.VariantId, quantity, facts.UnitNetMinor);
        if (index >= 0) items[index] = line;
        else
        {
            if (items.Count >= MaxLines) throw new ConflictException("CART_FULL", $"A cart holds at most {MaxLines} different items.");
            items.Add(line);
        }

        return new Cart(items, cart.Version + 1);
    }

    public static Cart Remove(Cart cart, Guid variantId) =>
        cart.Items.Any(i => i.VariantId == variantId)
            ? new Cart(cart.Items.Where(i => i.VariantId != variantId).ToList(), cart.Version + 1)
            : cart;

    /// <summary>
    /// Folds a guest cart into the signed-in buyer's cart on login. Quantities of the same variant add up (capped);
    /// the buyer's own lines keep their position, new guest lines follow in the guest's order. Safe to run twice on the
    /// same inputs: it depends only on its arguments.
    /// </summary>
    public static CartChange Merge(Cart user, Cart guest)
    {
        var notices = new List<CartNotice>();
        var items = user.Items.ToList();
        foreach (var g in guest.Items)
        {
            var index = items.FindIndex(i => i.VariantId == g.VariantId);
            if (index >= 0)
            {
                var sum = (long)items[index].Quantity + g.Quantity;
                var capped = (int)Math.Min(sum, MaxQuantityPerLine);
                if (capped < sum) notices.Add(new(CartNoticeKind.QuantityCapped, g.VariantId, sum, capped));
                items[index] = items[index] with { Quantity = capped };
            }
            else if (items.Count < MaxLines)
            {
                items.Add(g);
            }
        }

        return new CartChange(new Cart(items, Math.Max(user.Version, guest.Version) + 1), notices);
    }

    /// <summary>
    /// Brings every line in line with the catalog: drops what can no longer be sold, trims to stock, snaps to the pack
    /// size, refreshes the price. Each adjustment is returned as a notice so the UI can tell the buyer.
    /// </summary>
    public static CartChange Revalidate(Cart cart, IReadOnlyDictionary<Guid, VariantFacts> facts)
    {
        var notices = new List<CartNotice>();
        var kept = new List<CartItem>();

        foreach (var item in cart.Items)
        {
            if (!facts.TryGetValue(item.VariantId, out var f) || !f.Sellable)
            {
                notices.Add(new(CartNoticeKind.Unavailable, item.VariantId));
                continue;
            }

            if (f.Available <= 0)
            {
                notices.Add(new(CartNoticeKind.OutOfStock, item.VariantId));
                continue;
            }

            var min = Math.Max(1, f.MinOrderQty);
            var pack = Math.Max(1, f.PackSize);
            var quantity = item.Quantity;

            if (quantity > f.Available)
            {
                notices.Add(new(CartNoticeKind.StockReduced, item.VariantId, quantity, f.Available));
                quantity = f.Available;
            }

            var snapped = quantity / pack * pack;
            if (snapped != quantity)
            {
                notices.Add(new(CartNoticeKind.QuantityAdjustedToPack, item.VariantId, quantity, snapped));
                quantity = snapped;
            }

            if (quantity < min)
            {
                notices.Add(new(CartNoticeKind.BelowMinimumOrder, item.VariantId, quantity, min));
                continue;
            }

            if (item.SeenUnitNetMinor != f.UnitNetMinor)
                notices.Add(new(CartNoticeKind.PriceChanged, item.VariantId, item.SeenUnitNetMinor, f.UnitNetMinor));

            kept.Add(new CartItem(item.VariantId, quantity, f.UnitNetMinor));
        }

        var changed = notices.Count > 0;
        return new CartChange(changed ? new Cart(kept, cart.Version + 1) : cart, notices);
    }
}
