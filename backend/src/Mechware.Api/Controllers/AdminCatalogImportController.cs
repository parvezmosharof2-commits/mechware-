using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Catalog;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

public sealed class CatalogImportForm
{
    public IFormFile? File { get; set; }
}

/// <summary>
/// Bulk catalogue import (C10). The dry run checks a CSV against the catalogue rules and reports; it writes nothing.
/// Needs <c>catalog.manage</c>, checked here in the API. Format: docs/catalog/IMPORT_FORMAT.md.
/// </summary>
[ApiController]
[Route("api/v1/admin/catalog/import")]
public sealed class AdminCatalogImportController(ICatalogImportValidator validator, CatalogImportOptions options) : ControllerBase
{
    /// <summary>The default 10 MB file limit plus room for the multipart envelope.</summary>
    public const long MaxRequestBytes = 11L * 1024 * 1024;

    [HttpPost("dry-run")]
    [RequirePermission(Permissions.CatalogManage)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> DryRun([FromForm] CatalogImportForm form, CancellationToken ct)
    {
        if (form.File is null) throw new ValidationException("file", "Send the CSV in a form field called 'file'.");
        if (form.File.Length > options.MaxBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = new { code = "FILE_TOO_LARGE", message = "The file is too large." } });
        await using var content = form.File.OpenReadStream();
        return Ok(new { data = await validator.DryRunAsync(content, ct) });
    }
}
