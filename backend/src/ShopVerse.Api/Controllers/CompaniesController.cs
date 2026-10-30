using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopVerse.Application.Companies;
using ShopVerse.Application.Identity;

namespace ShopVerse.Api.Controllers;

/// <summary>
/// B2B company accounts, for members of a company. Access is decided by the caller's membership row in the
/// database (see <see cref="ICompanyService"/>); a company the caller does not belong to answers 404.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/companies")]
public sealed class CompaniesController(ICompanyService companies, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    [HttpPost]
    [ProducesResponseType(StatusCodes.Status201Created)]
    public async Task<IActionResult> Register([FromBody] RegisterCompanyRequest request, CancellationToken ct)
    {
        var created = await companies.RegisterAsync(Actor, request, ct);
        return Created($"/api/v1/companies/{created.Id}", new { data = created });
    }

    [HttpGet]
    public async Task<IActionResult> ListMine(CancellationToken ct) =>
        Ok(new { data = await companies.ListMineAsync(Actor, ct) });

    [HttpGet("{companyId:guid}")]
    public async Task<IActionResult> Get(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await companies.GetAsync(Actor, companyId, ct) });

    // ----- sites -----

    [HttpGet("{companyId:guid}/sites")]
    public async Task<IActionResult> ListSites(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await companies.ListSitesAsync(Actor, companyId, ct) });

    [HttpPost("{companyId:guid}/sites")]
    public async Task<IActionResult> AddSite(Guid companyId, [FromBody] SiteRequest request, CancellationToken ct)
    {
        var site = await companies.AddSiteAsync(Actor, companyId, request, ct);
        return Created($"/api/v1/companies/{companyId}/sites/{site.Id}", new { data = site });
    }

    [HttpPut("{companyId:guid}/sites/{siteId:guid}")]
    public async Task<IActionResult> UpdateSite(Guid companyId, Guid siteId, [FromBody] SiteRequest request, CancellationToken ct) =>
        Ok(new { data = await companies.UpdateSiteAsync(Actor, companyId, siteId, request, ct) });

    [HttpDelete("{companyId:guid}/sites/{siteId:guid}")]
    public async Task<IActionResult> RemoveSite(Guid companyId, Guid siteId, CancellationToken ct)
    {
        await companies.RemoveSiteAsync(Actor, companyId, siteId, ct);
        return NoContent();
    }

    // ----- cost centres -----

    [HttpGet("{companyId:guid}/cost-centres")]
    public async Task<IActionResult> ListCostCentres(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await companies.ListCostCentresAsync(Actor, companyId, ct) });

    [HttpPost("{companyId:guid}/cost-centres")]
    public async Task<IActionResult> AddCostCentre(Guid companyId, [FromBody] CostCentreRequest request, CancellationToken ct)
    {
        var cc = await companies.AddCostCentreAsync(Actor, companyId, request, ct);
        return Created($"/api/v1/companies/{companyId}/cost-centres/{cc.Id}", new { data = cc });
    }

    [HttpPut("{companyId:guid}/cost-centres/{costCentreId:guid}")]
    public async Task<IActionResult> UpdateCostCentre(Guid companyId, Guid costCentreId, [FromBody] CostCentreRequest request, CancellationToken ct) =>
        Ok(new { data = await companies.UpdateCostCentreAsync(Actor, companyId, costCentreId, request, ct) });

    [HttpDelete("{companyId:guid}/cost-centres/{costCentreId:guid}")]
    public async Task<IActionResult> RemoveCostCentre(Guid companyId, Guid costCentreId, CancellationToken ct)
    {
        await companies.RemoveCostCentreAsync(Actor, companyId, costCentreId, ct);
        return NoContent();
    }

    // ----- members -----

    [HttpGet("{companyId:guid}/members")]
    public async Task<IActionResult> ListMembers(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await companies.ListMembersAsync(Actor, companyId, ct) });

    [HttpPost("{companyId:guid}/members")]
    public async Task<IActionResult> AddMember(Guid companyId, [FromBody] AddMemberRequest request, CancellationToken ct)
    {
        var member = await companies.AddMemberAsync(Actor, companyId, request, ct);
        return Created($"/api/v1/companies/{companyId}/members/{member.Id}", new { data = member });
    }

    [HttpPut("{companyId:guid}/members/{memberId:guid}")]
    public async Task<IActionResult> UpdateMember(Guid companyId, Guid memberId, [FromBody] UpdateMemberRequest request, CancellationToken ct) =>
        Ok(new { data = await companies.UpdateMemberAsync(Actor, companyId, memberId, request, ct) });

    [HttpDelete("{companyId:guid}/members/{memberId:guid}")]
    public async Task<IActionResult> RemoveMember(Guid companyId, Guid memberId, CancellationToken ct)
    {
        await companies.RemoveMemberAsync(Actor, companyId, memberId, ct);
        return NoContent();
    }
}
