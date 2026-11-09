using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Identity;
using Mechware.Application.Payments;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Money going back to buyers, with four eyes (threat model v2, P3): support or finance asks (<c>orders.refund</c>), a different
/// person with <c>payments.refund_approve</c> approves, and only then does the provider move money.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminRefundsController(IRefundService refunds, ICurrentUserService currentUser) : ControllerBase
{
    private Guid Actor => currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");

    /// <summary>Payments holding money that must go back, and refunds not finished yet.</summary>
    [HttpGet("refunds")]
    [RequirePermission(Permissions.OrdersRefund)]
    public async Task<IActionResult> Queue(CancellationToken ct) => Ok(new { data = await refunds.QueueAsync(ct) });

    [HttpPost("payments/{paymentId:guid}/refunds")]
    [RequirePermission(Permissions.OrdersRefund)]
    public async Task<IActionResult> Request(Guid paymentId, [FromBody] RequestRefund body, CancellationToken ct)
    {
        var refund = await refunds.RequestAsync(Actor, paymentId, body, ct);
        return Created($"/api/v1/admin/refunds/{refund.Id}", new { data = refund });
    }

    [HttpPost("refunds/{refundId:guid}/approve")]
    [RequirePermission(Permissions.PaymentsRefundApprove)]
    [RequireStepUp("refund.approve")]
    public async Task<IActionResult> Approve(Guid refundId, CancellationToken ct) => Ok(new { data = await refunds.ApproveAsync(Actor, refundId, ct) });

    [HttpPost("refunds/{refundId:guid}/reject")]
    [RequirePermission(Permissions.PaymentsRefundApprove)]
    public async Task<IActionResult> Reject(Guid refundId, [FromBody] RejectRefund body, CancellationToken ct) =>
        Ok(new { data = await refunds.RejectAsync(Actor, refundId, body, ct) });
}
