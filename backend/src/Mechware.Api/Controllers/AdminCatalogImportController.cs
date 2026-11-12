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

public sealed class CatalogImportCommitForm
{
    public IFormFile? File { get; set; }

    /// <summary>The approved seller that owns the products the file creates (and that existing products must already belong to).</summary>
    public Guid? SellerId { get; set; }
}

/// <summary>
/// Bulk catalogue import (C10). The dry run checks a CSV against the catalogue rules and reports; it writes nothing. The commit
/// runs the same checks and then writes all of the file or none of it. Both need <c>catalog.manage</c>, checked here in the API.
/// No step-up code: no other catalogue edit asks for one (step-up guards credit, refunds and authenticator set-up), and an import
/// only creates drafts. Format: docs/catalog/IMPORT_FORMAT.md.
/// </summary>
[ApiController]
[Route("api/v1/admin/catalog/import")]
public sealed class AdminCatalogImportController(ICatalogImportValidator validator, ICatalogImportCommitter committer, CatalogImportOptions options) : ControllerBase
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

    [HttpPost("commit")]
    [RequirePermission(Permissions.CatalogManage)]
    [RequestSizeLimit(MaxRequestBytes)]
    [RequestFormLimits(MultipartBodyLengthLimit = MaxRequestBytes)]
    public async Task<IActionResult> Commit([FromForm] CatalogImportCommitForm form, CancellationToken ct)
    {
        if (form.File is null) throw new ValidationException("file", "Send the CSV in a form field called 'file'.");
        if (form.SellerId is not { } sellerId) throw new ValidationException("sellerId", "Send the seller that owns the products in a form field called 'sellerId'.");
        if (form.File.Length > options.MaxBytes) return StatusCode(StatusCodes.Status413PayloadTooLarge, new { error = new { code = "FILE_TOO_LARGE", message = "The file is too large." } });

        // At most 10 MB: held once to fingerprint it, then read by the checks.
        await using var content = new MemoryStream((int)form.File.Length);
        await form.File.CopyToAsync(content, ct);
        string sha256 = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(content.ToArray()));
        content.Position = 0;

        var outcome = await committer.CommitAsync(content, sha256, sellerId, ct);
        return outcome.Result is { } result
            ? Ok(new { data = result })
            : UnprocessableEntity(new { error = new { code = "IMPORT_REJECTED", message = "The file has errors; nothing was written." }, data = outcome.Rejected });
    }
}
