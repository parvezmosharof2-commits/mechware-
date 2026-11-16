using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Credit;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Credit adjustments (ADR-0010 §8.3, threat model §১২ CS20, CS21; B3.6): Finance or an Admin asks for a write-off, an on-account
/// correction or a payout (<c>credit.adjust</c>, Idempotency-Key, reason); ANOTHER Admin approves it with an authenticator code
/// (<c>credit.adjust_approve</c>), and only that approval writes the ledger line. A request is rejected or withdrawn without a code:
/// nothing moves.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminCreditAdjustmentsController(ICreditAdjustmentService adjustments) : ControllerBase
{
    public const string ApproveStepUpAction = "credit.adjustment.approve";

    [HttpPost("companies/{companyId:guid}/credit/adjustments")]
    [RequirePermission(Permissions.CreditAdjust)]
    public async Task<IActionResult> RequestAdjustment(Guid companyId, [FromBody] RequestCreditAdjustmentRequest body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var requested = await adjustments.RequestAsync(companyId, body, idempotencyKey, ct);
        return requested.Replayed
            ? Ok(new { data = requested.Adjustment })
            : Created($"/api/v1/admin/credit/adjustments/{requested.Adjustment.Id}", new { data = requested.Adjustment });
    }

    [HttpPost("credit/adjustments/{adjustmentId:guid}/approve")]
    [RequirePermission(Permissions.CreditAdjustApprove)]
    [RequireStepUp(ApproveStepUpAction)]
    public async Task<IActionResult> Approve(Guid adjustmentId, CancellationToken ct) =>
        Ok(new { data = await adjustments.ApproveAsync(adjustmentId, ct) });

    [HttpPost("credit/adjustments/{adjustmentId:guid}/reject")]
    [RequirePermission(Permissions.CreditAdjust)]
    public async Task<IActionResult> Reject(Guid adjustmentId, [FromBody] RejectCreditAdjustmentRequest body, CancellationToken ct) =>
        Ok(new { data = await adjustments.RejectAsync(adjustmentId, body, ct) });
}
