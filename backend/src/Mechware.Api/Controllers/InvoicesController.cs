using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Net.Http.Headers;
using Mechware.Application.Identity;
using Mechware.Application.Invoicing;

namespace Mechware.Api.Controllers;

/// <summary>
/// The buyer's invoices and credit notes (ADR-0012 §14; E6.8a). Per order: only the person who placed the order. Per company: only an effective
/// member whose role is in <c>Credit:BalanceVisibleTo</c> (Admin, Buyer). Everything else (unknown id, another person's order or company, a role
/// that may not see invoices, platform staff) is the same 404 body. Answers carry the printed content only; the buyer BIN is masked. Every
/// answer, errors too, is <c>Cache-Control: private, no-store</c> with <c>Vary: Authorization</c>. No lookup by number (numbers are guessable).
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1")]
public sealed class InvoicesController(IBuyerInvoiceReadService invoices, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    private void Private()
    {
        Response.Headers.CacheControl = "private, no-store";
        Response.Headers.Append(HeaderNames.Vary, "Authorization");
    }

    /// <summary>The invoices and credit notes of an order the caller placed, oldest first.</summary>
    [HttpGet("orders/{orderId:guid}/invoices")]
    public async Task<IActionResult> ForOrder(Guid orderId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await invoices.ListForOrderAsync(Actor, orderId, ct) });
    }

    [HttpGet("orders/{orderId:guid}/invoices/{invoiceId:guid}")]
    public async Task<IActionResult> OrderInvoice(Guid orderId, Guid invoiceId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await invoices.GetForOrderAsync(Actor, orderId, invoiceId, ct) });
    }

    /// <summary>The company's invoices, newest first. <c>from</c>, <c>to</c> (Dhaka days, inclusive), <c>page</c>, <c>pageSize</c> (at most 50).</summary>
    [HttpGet("companies/{companyId:guid}/invoices")]
    public async Task<IActionResult> ForCompany(Guid companyId, [FromQuery] string? from, [FromQuery] string? to, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default)
    {
        Private();
        return Ok(new { data = await invoices.ListForCompanyAsync(Actor, companyId, from, to, page, pageSize, ct) });
    }

    [HttpGet("companies/{companyId:guid}/invoices/{invoiceId:guid}")]
    public async Task<IActionResult> CompanyInvoice(Guid companyId, Guid invoiceId, CancellationToken ct)
    {
        Private();
        return Ok(new { data = await invoices.GetForCompanyAsync(Actor, companyId, invoiceId, ct) });
    }
}
