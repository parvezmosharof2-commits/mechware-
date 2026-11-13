using Mechware.Domain.Cart;
using Mechware.Domain.Common;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Application.Cart;

/// <summary>
/// Who owns a cart: a signed-in user or an anonymous guest. The only ways in are <see cref="ForUser"/> (a user id from
/// the authenticated principal, never from a request body) and <see cref="FromValidatedGuestToken"/> (a guest cart id
/// that <see cref="IGuestCartTokenService.Validate"/> has just verified). There is deliberately no constructor that
/// takes a raw client string.
/// </summary>
public readonly record struct CartOwner
{
    private CartOwner(CartOwnerKind kind, Guid id) { Kind = kind; Id = id; }

    public CartOwnerKind Kind { get; }
    public Guid Id { get; }

    public static CartOwner ForUser(Guid userId) =>
        userId == Guid.Empty ? throw new ValidationException("userId", "A user id is required.") : new(CartOwnerKind.User, userId);

    /// <summary>Only for the result of a successful <see cref="IGuestCartTokenService.Validate"/>.</summary>
    public static CartOwner FromValidatedGuestToken(GuestCartId guestCartId) => new(CartOwnerKind.Guest, guestCartId.Value);

    /// <summary>Store key, e.g. <c>cart:u:{id}</c> / <c>cart:g:{id}</c>; the kind prefix keeps the two id spaces apart.</summary>
    public string StoreKey => Kind == CartOwnerKind.User ? $"cart:u:{Id:N}" : $"cart:g:{Id:N}";
}

public enum CartOwnerKind { User = 1, Guest = 2 }

/// <summary>A random, unguessable guest cart id minted by the server (<see cref="IGuestCartTokenService.Issue"/>).</summary>
public readonly record struct GuestCartId(Guid Value)
{
    public static GuestCartId New() => new(Guid.NewGuid());
}

/// <summary>A cart as read from the store, with the opaque concurrency token that must be handed back on save.</summary>
/// <param name="ETag">Opaque; null never occurs for a stored cart. Callers must not interpret it.</param>
public sealed record StoredCart(Domain.Cart.Cart Cart, string ETag);

public enum CartSaveStatus { Saved = 1, Conflict = 2 }

/// <summary>
/// Cart persistence port (implemented on Redis in E1.2). Optimistic concurrency: a save only succeeds when the stored
/// cart still carries the ETag the caller read (<c>null</c> expected ETag = "the cart must not exist yet"). On
/// <see cref="CartSaveStatus.Conflict"/> nothing is written; the caller re-reads, re-applies its change and retries, so
/// two browser tabs can never silently overwrite each other.
///
/// Limits: a stored cart never exceeds <see cref="OrderLimits.MaxCartLines"/> lines nor
/// <see cref="OrderLimits.MaxQuantityPerLine"/> per line (the same limits <see cref="CartEngine"/> enforces); the store
/// rejects anything larger with a <see cref="ValidationException"/> as defence in depth.
///
/// TTL (sliding, refreshed on every save and read-with-touch): guest carts <see cref="GuestTtl"/>, user carts
/// <see cref="UserTtl"/>. An expired cart reads as absent (null), never as an error. Prices in a stored cart are only
/// "what the buyer last saw"; checkout re-prices on the server.
/// </summary>
public interface ICartStore
{
    public static readonly TimeSpan GuestTtl = TimeSpan.FromDays(7);
    public static readonly TimeSpan UserTtl = TimeSpan.FromDays(30);

    /// <summary>The stored cart, or null when none exists or it expired.</summary>
    Task<StoredCart?> GetAsync(CartOwner owner, CancellationToken ct = default);

    /// <summary>Compare-and-set. Returns the new ETag on success.</summary>
    Task<(CartSaveStatus Status, string? ETag)> SaveAsync(CartOwner owner, Domain.Cart.Cart cart, string? expectedETag, CancellationToken ct = default);

    /// <summary>Removes the cart (after checkout, or after a guest cart was merged into a user cart). Idempotent.</summary>
    Task DeleteAsync(CartOwner owner, CancellationToken ct = default);

    /// <summary>
    /// Commits a login merge in one atomic step (E1.4): stores <paramref name="merged"/> as the user's cart (compare-and-set on
    /// <paramref name="expectedUserETag"/>, null = must not exist) <b>and</b> deletes the guest cart, but only while the guest
    /// cart still carries <paramref name="guestETag"/>, the version that was merged. Either both happen or nothing is written
    /// (<see cref="CartSaveStatus.Conflict"/>): a guest cart can never be merged twice, and a guest change made after it was
    /// read is never deleted unmerged. Both keys must live on one Redis node (no Redis Cluster).
    /// </summary>
    Task<(CartSaveStatus Status, string? ETag)> SaveMergedAsync(
        CartOwner user, Domain.Cart.Cart merged, string? expectedUserETag, CartOwner guest, string guestETag, CancellationToken ct = default);
}

/// <summary>
/// Guest cart token contract (implemented in E1.2; no crypto here). The token is an opaque, signed (HMAC), expiring
/// string held in an HttpOnly cookie. <see cref="Validate"/> returns null for a forged, tampered, malformed or expired
/// token; it never throws on bad input and never reveals why it failed.
/// </summary>
public interface IGuestCartTokenService
{
    string Issue(GuestCartId guestCartId);

    GuestCartId? Validate(string? token);
}
