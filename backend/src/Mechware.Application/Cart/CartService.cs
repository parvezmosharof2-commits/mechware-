using Mechware.Domain.Cart;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Application.Cart;

/// <summary>What the catalog says about a variant right now, plus the two labels a cart line shows.</summary>
public sealed record CartVariantView(VariantFacts Facts, string Sku, string Title);

/// <summary>
/// Reads price, stock and sellable status for cart lines from the catalog. Prices are always the server's own (the same
/// <c>PricingEngine</c> quote checkout uses). A variant that does not exist and one that may not be sold look the same:
/// absent, or <c>Sellable = false</c>.
/// </summary>
public interface ICartCatalogReader
{
    Task<IReadOnlyDictionary<Guid, CartVariantView>> GetAsync(IReadOnlyCollection<Guid> variantIds, CancellationToken ct = default);
}

public sealed record CartLineView(Guid VariantId, string Sku, string Title, int Quantity, long UnitPriceMinor, long LineTotalMinor, int MinOrderQty, int PackSize);

public sealed record CartNoticeView(string Kind, Guid VariantId, long? Was, long? Now)
{
    /// <summary>The kind in camelCase, e.g. <c>priceChanged</c>, <c>quantityCapped</c>, <c>cartFull</c>.</summary>
    public static CartNoticeView From(CartNotice n) =>
        new(char.ToLowerInvariant(n.Kind.ToString()[0]) + n.Kind.ToString()[1..], n.VariantId, n.Was, n.Now);
}

/// <summary>The cart as the buyer sees it: current server prices, notices for anything that changed, and the totals.</summary>
/// <param name="ETag">Opaque version of the stored cart; null when there is no stored cart yet.</param>
public sealed record CartView(
    IReadOnlyList<CartLineView> Lines, IReadOnlyList<CartNoticeView> Notices, int LineCount, int TotalQuantity,
    long SubtotalMinor, string Currency, [property: System.Text.Json.Serialization.JsonPropertyName("etag")] string? ETag)
{
    public const string CurrencyCode = "BDT";
    public static CartView Empty { get; } = new([], [], 0, 0, 0, CurrencyCode, null);
}

public abstract record CartOperation
{
    /// <summary>Adds to the existing quantity.</summary>
    public sealed record Add(Guid VariantId, int Quantity) : CartOperation;

    /// <summary>Sets the quantity; zero removes the line.</summary>
    public sealed record Set(Guid VariantId, int Quantity) : CartOperation;

    public sealed record Remove(Guid VariantId) : CartOperation;
}

/// <summary>Per-IP limit on minting new guest carts (CT11). In-process, so the real ceiling is this number times the API replicas.</summary>
public interface IGuestCartCreationLimiter
{
    bool TryAcquire(string? ipAddress);
}

public interface ICartService
{
    /// <summary>The revalidated cart. Reads only: nothing is written and no cart is created.</summary>
    Task<CartView> GetAsync(CartOwner owner, CancellationToken ct = default);

    /// <summary>
    /// Applies one change on top of the latest stored cart (compare-and-set; on a conflict it re-reads and re-applies, at most
    /// <see cref="CartService.MaxAttempts"/> times, then 409 <c>CART_BUSY</c>). <paramref name="ifMatch"/>, when given, must equal
    /// the stored ETag or the call is 409 <c>CART_CHANGED</c>.
    /// </summary>
    Task<CartView> ApplyAsync(CartOwner owner, CartOperation operation, string? ifMatch, CancellationToken ct = default);

    Task<CartView> ClearAsync(CartOwner owner, CancellationToken ct = default);
}

public sealed class CartService(ICartStore store, ICartCatalogReader catalog) : ICartService
{
    public const int MaxAttempts = 3;

    public async Task<CartView> GetAsync(CartOwner owner, CancellationToken ct = default)
    {
        var stored = await store.GetAsync(owner, ct);
        if (stored is null) return CartView.Empty;
        var views = await catalog.GetAsync([.. stored.Cart.Items.Select(i => i.VariantId)], ct);
        var change = CartEngine.Revalidate(stored.Cart, Facts(views));
        return Build(change.Cart, change.Notices, views, stored.ETag);
    }

    public async Task<CartView> ApplyAsync(CartOwner owner, CartOperation operation, string? ifMatch, CancellationToken ct = default)
    {
        for (int attempt = 1; attempt <= MaxAttempts; attempt++)
        {
            var stored = await store.GetAsync(owner, ct);
            if (attempt == 1 && ifMatch is not null && stored?.ETag != ifMatch)
                throw new ConflictException("CART_CHANGED", "The cart changed since you loaded it. Reload and try again.");

            var current = stored?.Cart ?? Domain.Cart.Cart.Empty;
            var ids = current.Items.Select(i => i.VariantId).Append(TargetOf(operation)).Distinct().ToList();
            var views = await catalog.GetAsync(ids, ct);
            var facts = Facts(views);
            var revalidated = CartEngine.Revalidate(current, facts);

            var next = operation switch
            {
                CartOperation.Add a => CartEngine.Add(revalidated.Cart, Sellable(facts, a.VariantId), a.Quantity),
                CartOperation.Set s when s.Quantity == 0 => CartEngine.Remove(revalidated.Cart, s.VariantId),
                CartOperation.Set s => CartEngine.Set(revalidated.Cart, Sellable(facts, s.VariantId), s.Quantity),
                CartOperation.Remove r => CartEngine.Remove(revalidated.Cart, r.VariantId),
                _ => throw new ArgumentOutOfRangeException(nameof(operation))
            };

            // Nothing to store: a remove on a cart that never existed, or a no-op on one that already looks right.
            if (stored is null && next.Items.Count == 0) return CartView.Empty;
            if (stored is not null && ReferenceEquals(next, current)) return Build(next, [], views, stored.ETag);

            var (status, etag) = await store.SaveAsync(owner, next, stored?.ETag, ct);
            if (status == CartSaveStatus.Saved) return Build(next, revalidated.Notices, views, etag);
        }

        throw new ConflictException("CART_BUSY", "The cart is being changed from several places at once. Try again.");
    }

    public async Task<CartView> ClearAsync(CartOwner owner, CancellationToken ct = default)
    {
        await store.DeleteAsync(owner, ct);
        return CartView.Empty;
    }

    private static Guid TargetOf(CartOperation op) => op switch
    {
        CartOperation.Add a => a.VariantId,
        CartOperation.Set s => s.VariantId,
        CartOperation.Remove r => r.VariantId,
        _ => Guid.Empty
    };

    private static Dictionary<Guid, VariantFacts> Facts(IReadOnlyDictionary<Guid, CartVariantView> views) =>
        views.ToDictionary(v => v.Key, v => v.Value.Facts);

    /// <summary>Unknown, archived, draft and other sellers' unpublished items all answer the same way: not found.</summary>
    private static VariantFacts Sellable(Dictionary<Guid, VariantFacts> facts, Guid variantId) =>
        facts.TryGetValue(variantId, out var f) && f.Sellable ? f : throw new KeyNotFoundException("Item not found.");

    private static CartView Build(Domain.Cart.Cart cart, IReadOnlyList<CartNotice> notices, IReadOnlyDictionary<Guid, CartVariantView> views, string? etag)
    {
        var lines = cart.Items.Select(i =>
        {
            var v = views[i.VariantId];
            return new CartLineView(i.VariantId, v.Sku, v.Title, i.Quantity, v.Facts.UnitNetMinor, checked(i.Quantity * v.Facts.UnitNetMinor), v.Facts.MinOrderQty, v.Facts.PackSize);
        }).ToList();
        var noticeViews = notices.Select(CartNoticeView.From).ToList();
        return new CartView(lines, noticeViews, lines.Count, lines.Sum(l => l.Quantity), lines.Sum(l => l.LineTotalMinor), CartView.CurrencyCode, etag);
    }
}
