using Mechware.Domain.Cart;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Application.Cart;

/// <summary>What a login merge did: the size of the user's cart afterwards and anything the buyer should be told
/// (<c>quantityCapped</c>, <c>cartFull</c>). Stock, price and availability notices come with the next <c>GET /cart</c>.</summary>
public sealed record CartMergeResult(int LineCount, IReadOnlyList<CartNoticeView> Notices);

public interface ICartMergeService
{
    /// <summary>
    /// Folds the guest cart into the user's cart (<see cref="CartEngine.Merge"/>) and deletes the guest cart, as one atomic
    /// store step. Null when there is no guest cart (never made, expired, or already merged by another login). Throws when the
    /// store is unreachable or stays busy (409 <c>CART_BUSY</c>); nothing is lost then, the guest cart is still there.
    /// </summary>
    /// <param name="guestCartId">Only from a validated guest token (the HTTP layer reads the cookie).</param>
    Task<CartMergeResult?> MergeAsync(Guid userId, GuestCartId guestCartId, CancellationToken ct = default);
}

/// <summary>
/// E1.4. Read both carts, merge, then <see cref="ICartStore.SaveMergedAsync"/>: save the user cart only if it is unchanged
/// AND delete the guest cart only if it is the version that was merged, both or neither. A conflict (another tab changed
/// either cart, or a parallel login merged first) re-reads both and tries again, at most <see cref="CartService.MaxAttempts"/>
/// times. <see cref="CartEngine.Merge"/> adds quantities, so applying it twice would double them; the atomic commit is what
/// makes that impossible: once a merge is committed, the guest cart it merged no longer exists.
/// </summary>
public sealed class CartMergeService(ICartStore store) : ICartMergeService
{
    public async Task<CartMergeResult?> MergeAsync(Guid userId, GuestCartId guestCartId, CancellationToken ct = default)
    {
        var user = CartOwner.ForUser(userId);
        var guest = CartOwner.FromValidatedGuestToken(guestCartId);

        for (int attempt = 1; attempt <= CartService.MaxAttempts; attempt++)
        {
            var guestCart = await store.GetAsync(guest, ct);
            if (guestCart is null) return null;
            var userCart = await store.GetAsync(user, ct);

            var change = CartEngine.Merge(userCart?.Cart ?? Domain.Cart.Cart.Empty, guestCart.Cart);
            var (status, _) = await store.SaveMergedAsync(user, change.Cart, userCart?.ETag, guest, guestCart.ETag, ct);
            if (status == CartSaveStatus.Saved)
                return new CartMergeResult(change.Cart.Items.Count, [.. change.Notices.Select(CartNoticeView.From)]);
        }

        throw new ConflictException("CART_BUSY", "The cart is being changed from several places at once. Try again.");
    }
}
