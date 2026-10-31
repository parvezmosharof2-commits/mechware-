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
    private const int MaxWebhookBytes = 64 * 1024;

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
        // A webhook is a few hundred bytes. Read at most the cap plus one byte so an oversized or endless body is refused
        // instead of being buffered, whether or not the client declared a length.
        var buffer = new byte[MaxWebhookBytes + 1];
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await Request.Body.ReadAsync(buffer.AsMemory(read, buffer.Length - read), ct);
            if (n == 0) break;
            read += n;
        }

        if (read > MaxWebhookBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge);
        var raw = System.Text.Encoding.UTF8.GetString(buffer, 0, read);

        var outcome = await payments.HandleWebhookAsync(
            provider, Request.Headers["X-Signature"].FirstOrDefault(), Request.Headers["X-Timestamp"].FirstOrDefault(), raw, ct);

        // Providers retry anything that is not 2xx, so every handled event, including duplicates and unknown payments, is 200.
        return Ok(new { received = true, outcome = outcome.ToString() });
    }
}
