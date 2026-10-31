using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopVerse.Application.Identity;
using ShopVerse.Application.Payments;

namespace ShopVerse.Api.Controllers;

public sealed record StartPaymentBody(string Provider);

[ApiController]
[Route("api/v1")]
public sealed class PaymentsController(IPaymentService payments, ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>Starts paying the caller's pending order. The amount is the order's own total; it cannot be chosen here.</summary>
    [Authorize]
    [HttpPost("orders/{orderId:guid}/payments")]
    public async Task<IActionResult> Start(Guid orderId, [FromBody] StartPaymentBody body, CancellationToken ct)
    {
        var userId = currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        var session = await payments.StartAsync(userId, orderId, body.Provider, ct);
        return Created($"/api/v1/orders/{orderId}", new { data = session });
    }

    /// <summary>
    /// The provider's server-to-server notification. Anonymous by nature: its authority is the signature, which is checked
    /// over the exact raw body, so the body is read as text rather than bound to a model.
    /// </summary>
    [AllowAnonymous]
    [HttpPost("payments/webhooks/{provider}")]
    public async Task<IActionResult> Webhook(string provider, CancellationToken ct)
    {
        using var reader = new StreamReader(Request.Body);
        var raw = await reader.ReadToEndAsync(ct);

        var outcome = await payments.HandleWebhookAsync(
            provider, Request.Headers["X-Signature"].FirstOrDefault(), Request.Headers["X-Timestamp"].FirstOrDefault(), raw, ct);

        // Providers retry anything that is not 2xx, so every handled event, including duplicates and unknown payments, is 200.
        return Ok(new { received = true, outcome = outcome.ToString() });
    }
}
