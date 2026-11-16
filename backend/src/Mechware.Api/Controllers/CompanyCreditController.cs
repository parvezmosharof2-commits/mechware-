using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mechware.Application.Credit;
using Mechware.Application.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// A company's own credit position for its members (ADR-0010 §10, threat model CS22-CS24; B3.9). Read-only. Only a member of the company with a role in
/// <c>Credit:BalanceVisibleTo</c> (default Admin or Buyer) gets an answer; an unknown company, a non-member, any other role and a Pending or Rejected
/// company all get the same 404 body, and a Suspended company still sees its debt. Platform staff roles give no access here (the staff reads are
/// <c>admin/credit/...</c>). Every answer, errors too, is <c>Cache-Control: private, no-store</c> with <c>Vary: Authorization</c>.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/companies/{companyId:guid}/credit")]
public sealed class CompanyCreditController(IBuyerCreditService credit, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private void NoStore()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
    }

    [HttpGet]
    public async Task<IActionResult> Get(Guid companyId, CancellationToken ct)
    {
        NoStore();
        return Ok(new { data = await credit.GetAsync(Actor, companyId, ct) });
    }

    /// <summary>Orders that still owe something, oldest due date first; <c>page</c>, <c>pageSize</c> (at most 100).</summary>
    [HttpGet("open-items")]
    public async Task<IActionResult> OpenItems(Guid companyId, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        NoStore();
        return Ok(new { data = await credit.OpenItemsAsync(Actor, companyId, page, pageSize, ct) });
    }

    /// <summary>Ledger lines in posting order, optionally between two Dhaka days (<c>from</c>, <c>to</c>, yyyy-MM-dd); <c>page</c>, <c>pageSize</c> (at most 100).</summary>
    [HttpGet("statement")]
    public async Task<IActionResult> Statement(Guid companyId, [FromQuery] DateOnly? from, [FromQuery] DateOnly? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        NoStore();
        return Ok(new { data = await credit.StatementAsync(Actor, companyId, from, to, page, pageSize, ct) });
    }
}
