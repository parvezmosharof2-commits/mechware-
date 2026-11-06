using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;
using ShopVerse.Api.Authorization;
using ShopVerse.Application.Audit;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Api.Controllers;

/// <summary>Whether the audit log is intact (threat model v1, H3): recomputes the hash chain over every sealed row.</summary>
[ApiController]
[Route("api/v1/admin/audit")]
public sealed class AdminAuditController(IAuditChain chain, IMemoryCache cache) : ControllerBase
{
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
}
