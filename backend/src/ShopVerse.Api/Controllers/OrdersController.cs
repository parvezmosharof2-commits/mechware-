using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopVerse.Application.Identity;
using ShopVerse.Application.Orders;
using ShopVerse.Domain.Common.Exceptions;

namespace ShopVerse.Api.Controllers;

/// <summary>
/// What the buyer asks for: items and quantities, and which saved address or company site to deliver to. Every price,
/// tax, discount, total and the address text itself are the server's own; other JSON members are not bound and are ignored.
/// </summary>
public sealed record PlaceOrderBody(IReadOnlyList<PlaceOrderLine> Lines, Guid? ShippingAddressId = null, Guid? CompanyId = null, bool PayOnCredit = false, IReadOnlyList<string>? CouponCodes = null, Guid? ShipToSiteId = null);

public sealed record CancelOrderBody(string? Reason);

[ApiController]
[Authorize]
[Route("api/v1/orders")]
public sealed class OrdersController(IOrderService orders, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    /// <summary>Places an order. The <c>Idempotency-Key</c> header is required: retrying a request with the same key and body returns the first order.</summary>
    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> Place([FromBody] PlaceOrderBody body, [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(idempotencyKey))
            throw new ValidationException("Idempotency-Key", "The Idempotency-Key header is required.");

        var placed = await orders.PlaceAsync(new PlaceOrderRequest(Actor, idempotencyKey, body.Lines ?? [], body.ShippingAddressId, body.CompanyId, body.PayOnCredit, body.CouponCodes, body.ShipToSiteId), ct);

        // A replay is not a new resource: 200 with the original order, 201 only for the request that created it.
        return placed.Replayed
            ? Ok(new { data = placed })
            : Created($"/api/v1/orders/{placed.OrderId}", new { data = placed });
    }

    /// <summary>Cancels the caller's own order while it is still pending.</summary>
    [HttpPost("{orderId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid orderId, [FromBody] CancelOrderBody body, CancellationToken ct) =>
        Ok(new { data = await orders.CancelAsync(Actor, orderId, body?.Reason ?? string.Empty, ct) });

    [HttpGet("{orderId:guid}")]
    public async Task<IActionResult> Get(Guid orderId, CancellationToken ct) =>
        Ok(new { data = await orders.GetAsync(Actor, orderId, ct) });
}
