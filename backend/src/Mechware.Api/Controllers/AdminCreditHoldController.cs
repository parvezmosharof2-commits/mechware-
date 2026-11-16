using Microsoft.AspNetCore.Mvc;
using Mechware.Api.Authorization;
using Mechware.Application.Credit;
using Mechware.Domain.Identity;

namespace Mechware.Api.Controllers;

/// <summary>
/// The overdue credit hold (ADR-0010 §9, threat model §১২ CS25; B3.7): only an Admin lifts it, with a fresh authenticator code and a
/// reason, for at most <c>Credit:HoldOverrideMaxDays</c> (7) days; 0 ends a lift. Every call is audited.
/// </summary>
[ApiController]
[Route("api/v1/admin")]
public sealed class AdminCreditHoldController(ICreditHoldService holds) : ControllerBase
{
    public const string OverrideStepUpAction = "credit.hold.override";

    [HttpPut("companies/{companyId:guid}/credit/hold-override")]
    [RequirePermission(Permissions.CreditHoldOverride)]
    [RequireStepUp(OverrideStepUpAction)]
    public async Task<IActionResult> Override(Guid companyId, [FromBody] OverrideCreditHoldRequest body, CancellationToken ct) =>
        Ok(new { data = await holds.OverrideAsync(companyId, body, ct) });
}
