using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using ShopVerse.Application.Approvals;
using ShopVerse.Application.Identity;

namespace ShopVerse.Api.Controllers;

/// <summary>
/// Approval rules and requests inside one company (brief §8.3). Access comes from the caller's company
/// membership in the database; a company the caller does not belong to answers 404.
/// </summary>
[ApiController]
[Authorize]
[Route("api/v1/companies/{companyId:guid}/approvals")]
public sealed class ApprovalsController(IApprovalService approvals, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    // ----- rules (company admins) -----

    [HttpGet("rules")]
    public async Task<IActionResult> ListRules(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await approvals.ListRulesAsync(Actor, companyId, ct) });

    [HttpPost("rules")]
    public async Task<IActionResult> AddRule(Guid companyId, [FromBody] ApprovalRuleRequest request, CancellationToken ct)
    {
        var rule = await approvals.AddRuleAsync(Actor, companyId, request, ct);
        return Created($"/api/v1/companies/{companyId}/approvals/rules/{rule.Id}", new { data = rule });
    }

    [HttpPut("rules/{ruleId:guid}")]
    public async Task<IActionResult> UpdateRule(Guid companyId, Guid ruleId, [FromBody] ApprovalRuleRequest request, CancellationToken ct) =>
        Ok(new { data = await approvals.UpdateRuleAsync(Actor, companyId, ruleId, request, ct) });

    [HttpDelete("rules/{ruleId:guid}")]
    public async Task<IActionResult> RemoveRule(Guid companyId, Guid ruleId, CancellationToken ct)
    {
        await approvals.RemoveRuleAsync(Actor, companyId, ruleId, ct);
        return NoContent();
    }

    // ----- requests -----

    /// <summary>Creates an approval request if a rule matches; otherwise answers <c>approvalRequired: false</c>.</summary>
    [HttpPost("requests")]
    public async Task<IActionResult> Submit(Guid companyId, [FromBody] SubmitApprovalRequest request, CancellationToken ct)
    {
        var result = await approvals.SubmitAsync(Actor, companyId, request, ct);
        return result.ApprovalRequired
            ? Created($"/api/v1/companies/{companyId}/approvals/requests/{result.Request!.Id}", new { data = result })
            : Ok(new { data = result });
    }

    [HttpGet("requests/mine")]
    public async Task<IActionResult> ListMine(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await approvals.ListMineAsync(Actor, companyId, ct) });

    [HttpGet("requests/inbox")]
    public async Task<IActionResult> ListInbox(Guid companyId, CancellationToken ct) =>
        Ok(new { data = await approvals.ListInboxAsync(Actor, companyId, ct) });

    [HttpGet("requests/{requestId:guid}")]
    public async Task<IActionResult> Get(Guid companyId, Guid requestId, CancellationToken ct) =>
        Ok(new { data = await approvals.GetAsync(Actor, companyId, requestId, ct) });

    [HttpPost("requests/{requestId:guid}/decision")]
    public async Task<IActionResult> Decide(Guid companyId, Guid requestId, [FromBody] DecisionRequest request, CancellationToken ct) =>
        Ok(new { data = await approvals.DecideAsync(Actor, companyId, requestId, request, ct) });

    [HttpPost("requests/{requestId:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid companyId, Guid requestId, CancellationToken ct) =>
        Ok(new { data = await approvals.CancelAsync(Actor, companyId, requestId, ct) });
}
