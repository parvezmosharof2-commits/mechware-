using Microsoft.AspNetCore.Mvc;
using ShopVerse.Api.Authorization;
using ShopVerse.Application.Companies;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Api.Controllers;

/// <summary>MECHWARE staff view of company accounts: verification, suspension and credit terms (finance only).</summary>
[ApiController]
[Route("api/v1/admin/companies")]
public sealed class AdminCompaniesController(ICompanyService companies) : ControllerBase
{
    [HttpGet]
    [RequirePermission(Permissions.CompaniesView)]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default) =>
        Ok(new { data = await companies.AdminListAsync(status, page, pageSize, ct) });

    [HttpGet("{companyId:guid}")]
    [RequirePermission(Permissions.CompaniesView)]
    public async Task<IActionResult> Get(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await companies.AdminGetAsync(companyId, ct) });

    [HttpPost("{companyId:guid}/activate")]
    [RequirePermission(Permissions.CompaniesManage)]
    public async Task<IActionResult> Activate(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await companies.AdminActivateAsync(companyId, ct) });

    [HttpPost("{companyId:guid}/suspend")]
    [RequirePermission(Permissions.CompaniesManage)]
    public async Task<IActionResult> Suspend(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await companies.AdminSuspendAsync(companyId, ct) });

    [HttpPut("{companyId:guid}/credit")]
    [RequirePermission(Permissions.CompaniesCredit)]
    public async Task<IActionResult> SetCredit(Guid companyId, [FromBody] SetCreditRequest request, CancellationToken ct) =>
        Ok(new { data = await companies.AdminSetCreditAsync(companyId, request, ct) });
}
