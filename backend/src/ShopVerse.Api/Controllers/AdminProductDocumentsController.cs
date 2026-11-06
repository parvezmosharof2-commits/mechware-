using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc;
using ShopVerse.Api.Authorization;
using ShopVerse.Application.Catalog;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure;

namespace ShopVerse.Api.Controllers;

public sealed class UploadDocumentForm
{
    /// <summary><c>datasheet</c>, <c>cad</c> or <c>certificate</c>.</summary>
    public string? Kind { get; set; }

    public IFormFile? File { get; set; }
}

/// <summary>
/// Staff attach datasheets, CAD models and certificates to a product (sprint 3–4). Every action needs <c>catalog.manage</c>.
/// The upload is scanned and checked before anything is stored; see <see cref="IProductDocumentService.UploadAsync"/>.
/// </summary>
[ApiController]
[Route("api/v1/admin/products/{productId:guid}/documents")]
public sealed class AdminProductDocumentsController(IProductDocumentService documents, ICurrentUserService currentUser) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> List(Guid productId, CancellationToken ct) =>
        Ok(new { data = await documents.ListAsync(productId, ct) });

    [HttpPost]
    [RequirePermission(Permissions.CatalogManage)]
    [RequestSizeLimit(DependencyInjection.MaxUploadRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = DependencyInjection.MaxUploadRequestBytes)]
    public async Task<IActionResult> Upload(Guid productId, [FromForm] UploadDocumentForm form, CancellationToken ct)
    {
        if (form.File is null) throw new ValidationException("file", "Send the file in a form field called 'file'.");
        await using var content = form.File.OpenReadStream();
        var document = await documents.UploadAsync(productId, form.Kind, form.File.FileName, content, currentUser.UserId, ct);
        return Created($"/api/v1/admin/products/{productId}/documents/{document.Id}", new { data = document });
    }

    [HttpDelete("{documentId:guid}")]
    [RequirePermission(Permissions.CatalogManage)]
    public async Task<IActionResult> Delete(Guid productId, Guid documentId, CancellationToken ct)
    {
        await documents.DeleteAsync(productId, documentId, ct);
        return NoContent();
    }
}
