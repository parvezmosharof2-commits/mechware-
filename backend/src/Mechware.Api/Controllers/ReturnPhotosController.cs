using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mechware.Api.Authorization;
using Mechware.Application.Identity;
using Mechware.Application.Orders;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

public sealed class UploadReturnPhotoForm
{
    /// <summary>A JPEG, PNG or WebP photo of at most 5 MB; its name and claimed type are ignored.</summary>
    public IFormFile? File { get; set; }
}

/// <summary>
/// F2.2 the buyer's photos of their own return: up to five while it is Requested or Approved, removable while Requested. Anything the
/// caller may not see is the same 404. Every answer is <c>private, no-store</c>; a photo is served <c>inline</c> as <c>image/jpeg</c>
/// (the server's own re-encoding) with <c>nosniff</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/returns/{returnId:guid}/photos")]
public sealed class ReturnPhotosController(IReturnPhotoService photos, ICurrentUserService currentUser) : ControllerBase
{
    /// <summary>Request ceiling: the 5 MB photo plus the form around it.</summary>
    public const long MaxRequestBytes = 6L * 1024 * 1024;

    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    /// <summary>
    /// Multipart field <c>file</c>. 413 <c>FILE_TOO_LARGE</c>; 400 not JPEG/PNG/WebP by its first bytes, infected or unreadable; 503
    /// <c>SCAN_UNAVAILABLE</c>; 409 <c>RETURN_PHOTOS_CLOSED</c> (received or later, withdrawn, rejected), <c>RETURN_PHOTOS_FULL</c> (five).
    /// </summary>
    [HttpPost]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Upload(Guid returnId, [FromForm] UploadReturnPhotoForm form, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        if (form.File is null) throw new ValidationException("file", "Send the photo in a form field called 'file'.");
        await using var content = form.File.OpenReadStream();
        var photo = await photos.UploadAsync(Actor, returnId, content, ct);
        return Created($"/api/v1/returns/{returnId}/photos/{photo.Id}", new { data = photo });
    }

    [HttpGet]
    public async Task<IActionResult> List(Guid returnId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = await photos.ListOwnAsync(Actor, returnId, ct) });
    }

    [HttpGet("{photoId:guid}")]
    public async Task<IActionResult> Get(Guid returnId, Guid photoId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";   // the 404 too
        return ReturnPhotoResponse.Inline(this, await photos.OpenOwnAsync(Actor, returnId, photoId, ct));
    }

    /// <summary>Only before staff decide (409 <c>RETURN_PHOTOS_CLOSED</c> after).</summary>
    [HttpDelete("{photoId:guid}")]
    public async Task<IActionResult> Delete(Guid returnId, Guid photoId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        await photos.DeleteOwnAsync(Actor, returnId, photoId, ct);
        return NoContent();
    }
}

/// <summary>F2.2 staff view of a return's photos (<c>orders.view_all</c>: Support, Finance, Admin). Staff cannot add or remove them.</summary>
[ApiController]
[Route("api/v1/admin/returns/{returnId:guid}/photos")]
public sealed class AdminReturnPhotosController(IReturnPhotoService photos) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.OrdersViewAll)]
    public async Task<IActionResult> List(Guid returnId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = await photos.ListStaffAsync(returnId, ct) });
    }

    [HttpGet("{photoId:guid}")]
    [RequirePermission(Permissions.OrdersViewAll)]
    public async Task<IActionResult> Get(Guid returnId, Guid photoId, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        return ReturnPhotoResponse.Inline(this, await photos.OpenStaffAsync(returnId, photoId, ct));
    }
}

internal static class ReturnPhotoResponse
{
    /// <summary>Shown in the page, never cached, never sniffed: the type is the one the server encoded.</summary>
    public static IActionResult Inline(ControllerBase controller, ReturnPhotoFile file)
    {
        var headers = controller.Response.Headers;
        headers.CacheControl = "private, no-store";
        headers.ContentDisposition = new ContentDispositionHeaderValue("inline") { FileName = $"return-photo-{file.Id:N}.jpg" }.ToString();
        headers.XContentTypeOptions = "nosniff";
        return controller.File(file.Content, file.ContentType, enableRangeProcessing: false);
    }
}
