using Mechware.Application.Identity;
using Mechware.Application.Notifications;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;

namespace Mechware.Api.Controllers;

/// <summary>
/// The signed-in user's in-app notifications (F3c). Own rows only: another user's id and an unknown id give the same 404. Every answer is
/// <c>Cache-Control: private, no-store</c>. Items hold a title key and values, never HTML; the web renders them in EN or BN.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/notifications")]
public sealed class NotificationsController(IInAppNotificationService notifications, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private void Private()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
    }

    /// <summary>Newest first, 50 per page. <c>unread=true</c> keeps only unread ones.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] bool unread = false, [FromQuery] int page = 1, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await notifications.ListAsync(Actor, unread, page, ct) });
    }

    [HttpGet("unread-count")]
    public async Task<IActionResult> UnreadCount(CancellationToken ct)
    {
        Private();
        return Ok(new { data = new { count = await notifications.UnreadCountAsync(Actor, ct) } });
    }

    [HttpPost("{id:guid}/read")]
    public async Task<IActionResult> Read(Guid id, CancellationToken ct)
    {
        Private();
        await notifications.MarkReadAsync(Actor, id, ct);
        return NoContent();
    }

    [HttpPost("read-all")]
    public async Task<IActionResult> ReadAll(CancellationToken ct)
    {
        Private();
        return Ok(new { data = new { updated = await notifications.MarkAllReadAsync(Actor, ct) } });
    }
}
