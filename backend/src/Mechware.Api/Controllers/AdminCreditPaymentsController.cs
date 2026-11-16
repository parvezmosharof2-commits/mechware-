using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Credit;
using Mechware.Application.Identity;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// Customer payments against credit (ADR-0010 §4-§7, threat model §১২ CS1-CS11). Finance records a cheque or transfer
/// (<c>credit.record_payment</c>, Idempotency-Key required); a DIFFERENT person verifies it (<c>credit.verify_payment</c> plus an
/// authenticator code, owner decision 3), and only that verification releases credit. A mistaken entry is rejected (four eyes
/// too, no code: nothing is released) and recorded again.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminCreditPaymentsController(ICreditPaymentService payments, IIdentityService identity, ICurrentUserService currentUser) : ControllerBase
{
    public const string VerifyStepUpAction = "credit.payment.verify";

    [HttpPost("companies/{companyId:guid}/credit/payments")]
    [RequirePermission(Permissions.CreditRecordPayment)]
    public async Task<IActionResult> Record(Guid companyId, [FromBody] RecordCreditPaymentRequest body,
        [FromHeader(Name = "Idempotency-Key")] string? idempotencyKey, CancellationToken ct)
    {
        var recorded = await payments.RecordAsync(companyId, body, idempotencyKey, ct);
        return recorded.Replayed
            ? Ok(new { data = recorded.Payment })
            : Created($"/api/v1/admin/credit/payments/{recorded.Payment.Id}", new { data = recorded.Payment });
    }

    [HttpPost("credit/payments/{paymentId:guid}/verify")]
    [RequirePermission(Permissions.CreditVerifyPayment)]
    public async Task<IActionResult> Verify(Guid paymentId, [FromBody] VerifyCreditPaymentRequest body, CancellationToken ct)
    {
        // Like [RequireStepUp], but owner decision 3 may limit it to larger amounts; a payment's amount never changes once recorded.
        if (await payments.VerificationNeedsStepUpAsync(paymentId, ct))
            await identity.VerifyStepUpAsync(currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required."),
                Request.Headers[RequireStepUpAttribute.HeaderName].FirstOrDefault(), VerifyStepUpAction, $"paymentId={paymentId}",
                currentUser.IpAddress, cancellationToken: ct);
        return Ok(new { data = await payments.VerifyAsync(paymentId, body, ct) });
    }

    [HttpPost("credit/payments/{paymentId:guid}/reject")]
    [RequirePermission(Permissions.CreditVerifyPayment)]
    public async Task<IActionResult> Reject(Guid paymentId, [FromBody] RejectCreditPaymentRequest body, CancellationToken ct) =>
        Ok(new { data = await payments.RejectAsync(paymentId, body, ct) });
}
