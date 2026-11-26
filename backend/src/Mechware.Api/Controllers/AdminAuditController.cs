using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Caching.Memory;
using Mechware.Api.Authorization;
using Mechware.Application.Audit;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// The audit log for staff with audit.view: whether it is intact (threat model v1, H3: recomputes the hash chain over every sealed row),
/// and the rows themselves (G5.2b), redacted and never decrypted.
/// </summary>
[ApiController]
[Route("api/v1/admin/audit")]
public sealed class AdminAuditController(IAuditChain chain, IMemoryCache cache, IAuditLogReadService reads) : ControllerBase
{
    public const string ReadRateLimitPolicy = "AuditRead";

    /// <summary>A full check reads the whole log, so one result serves every caller for a few minutes.</summary>
    public static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(5);

    [HttpGet("chain")]
    [RequirePermission(Permissions.AuditView)]
    public async Task<IActionResult> Verify(CancellationToken ct)
    {
        var status = (await cache.GetOrCreateAsync("audit-chain-status", entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = CacheFor;
            return chain.VerifyAsync(ct);
        }))!;
        return Ok(new { data = new { status.Sealed, status.Unsealed, status.Intact, status.FirstBrokenPosition, status.HeadHash } });
    }

    /// <summary>
    /// Audit rows, newest first (G5.2b). Filters (exact matches): <c>actorId</c>, <c>action</c>, <c>entityName</c>, <c>entityId</c>,
    /// <c>correlationId</c>; <c>from</c>/<c>to</c> are Dhaka dates (yyyy-MM-dd, inclusive). <c>pageSize</c> at most 50. No values (see the
    /// detail). A bad filter is 400. The read is itself audited (<c>audit.searched</c>). <c>private, no-store</c>.
    /// </summary>
    [HttpGet]
    [RequirePermission(Permissions.AuditView)]
    [EnableRateLimiting(ReadRateLimitPolicy)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? actorId, [FromQuery] string? action, [FromQuery] string? entityName, [FromQuery] string? entityId,
        [FromQuery] string? correlationId, [FromQuery] string? from, [FromQuery] string? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        var result = await reads.ListAsync(new AuditLogFilter(actorId, action, entityName, entityId, correlationId, from, to, page, pageSize), ct);
        Response.Headers.CacheControl = "private, no-store";
        return Ok(new { data = result });
    }

    /// <summary>
    /// One audit row with <c>oldValues</c>/<c>newValues</c> as JSON, redacted on the server: secrets, codes, hashes, tokens, encrypted business
    /// numbers and bank references read "[hidden]"; nothing is decrypted. 404 for an unknown id. Audited (<c>audit.viewed</c>). <c>private, no-store</c>.
    /// </summary>
    [HttpGet("{id:guid}")]
    [RequirePermission(Permissions.AuditView)]
    [EnableRateLimiting(ReadRateLimitPolicy)]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct)
    {
        Response.Headers.CacheControl = "private, no-store";
        var result = await reads.GetAsync(id, ct);
        return Ok(new { data = result });
    }
}
