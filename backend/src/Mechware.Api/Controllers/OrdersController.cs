using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;

namespace Mechware.Api.Controllers;

/// <summary>
/// What the buyer asks for: items and quantities, and which saved address or company site to deliver to. Every price,
/// tax, discount, total and the address text itself are the server's own; other JSON members are not bound and are ignored.
/// </summary>
public sealed record PlaceOrderBody(IReadOnlyList<PlaceOrderLine> Lines, Guid? ShippingAddressId = null, Guid? CompanyId = null, bool PayOnCredit = false, IReadOnlyList<string>? CouponCodes = null, Guid? ShipToSiteId = null, Guid? CostCentreId = null, string? PurchaseOrderNumber = null);

/// <summary>The lines and destination to price delivery for. Like an order, it carries no money: shipping is the server's own.</summary>
public sealed record ShippingQuoteBody(IReadOnlyList<PlaceOrderLine> Lines, Guid? ShippingAddressId = null, Guid? CompanyId = null, IReadOnlyList<string>? CouponCodes = null, Guid? ShipToSiteId = null);

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

        var placed = await orders.PlaceAsync(new PlaceOrderRequest(Actor, idempotencyKey, body.Lines ?? [], body.ShippingAddressId, body.CompanyId, body.PayOnCredit, body.CouponCodes, body.ShipToSiteId, body.CostCentreId, body.PurchaseOrderNumber), ct);

        // A replay is not a new resource: 200 with the original order, 201 only for the request that created it.
        return placed.Replayed
            ? Ok(new { data = placed })
            : Created($"/api/v1/orders/{placed.OrderId}", new { data = placed });
    }

    /// <summary>
    /// What delivery would cost for these lines and destination, per seller, without placing an order or holding stock (E8.2).
    /// Refused with 422 <c>SHIPPING_NOT_AVAILABLE</c>, <c>SHIPPING_QUOTE_ONLY</c> or <c>VARIANT_WEIGHT_MISSING</c>, as placement would be.
    /// </summary>
    [HttpPost("shipping-quote")]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> QuoteShipping([FromBody] ShippingQuoteBody body, CancellationToken ct) =>
        Ok(new { data = await orders.QuoteShippingAsync(new ShippingQuoteRequest(Actor, body.Lines ?? [], body.ShippingAddressId, body.CompanyId, body.CouponCodes, body.ShipToSiteId), ct) });

    /// <summary>Cancels the caller's own order while it is still pending.</summary>
    [HttpPost("{orderId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid orderId, [FromBody] CancelOrderBody body, CancellationToken ct) =>
        Ok(new { data = await orders.CancelAsync(Actor, orderId, body?.Reason ?? string.Empty, ct) });

    [HttpGet("{orderId:guid}")]
    public async Task<IActionResult> Get(Guid orderId, CancellationToken ct) =>
        Ok(new { data = await orders.GetAsync(Actor, orderId, ct) });
}
