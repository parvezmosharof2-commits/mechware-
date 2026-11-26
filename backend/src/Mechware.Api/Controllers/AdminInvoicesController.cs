using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mechware.Api.Authorization;
using Mechware.Application.Invoicing;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Staff reads of invoices and credit notes (ADR-0012 §14; E6.8a), behind <c>invoices.view</c> (Support, Finance, Admin). Read-only. The
/// detail carries the whole buyer BIN, the content hash and the issuing user; a buyer never gets those. Answers are <c>private, no-store</c>.
/// </summary>
[ApiController]
[Route("api/v1/admin/invoices")]
[RequirePermission(Permissions.InvoicesView)]
public sealed class AdminInvoicesController(IStaffInvoiceReadService invoices) : ControllerBase
{
    private void Private()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
    }

    /// <summary>
    /// Newest first. Filters: <c>number</c>, <c>orderNumber</c> (prefixes), <c>companyId</c>, <c>from</c>/<c>to</c> (Dhaka days, inclusive), <c>kind</c>
    /// (tax_invoice, credit_note), <c>mode</c> (live, specimen). <c>pageSize</c> at most 50. A bad filter is 400.
    /// </summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? number, [FromQuery] string? orderNumber, [FromQuery] Guid? companyId, [FromQuery] string? from,
        [FromQuery] string? to, [FromQuery] string? kind, [FromQuery] string? mode, [FromQuery] int page = 1, [FromQuery] int pageSize = 25, CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await invoices.ListAsync(new StaffInvoiceFilter(number, orderNumber, companyId, from, to, kind, mode, page, pageSize), ct) });
    }

    [HttpGet("{invoiceId:guid}")]
    public async Task<IActionResult> Get(Guid invoiceId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await invoices.GetAsync(invoiceId, ct) });
    }
}
