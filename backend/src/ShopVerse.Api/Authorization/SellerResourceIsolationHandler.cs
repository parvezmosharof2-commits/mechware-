using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Identity;

namespace ShopVerse.Api.Authorization;

public sealed class SellerResourceIsolationRequirement : IAuthorizationRequirement
{
    public static readonly SellerResourceIsolationRequirement Instance = new();
}

/// <summary>
/// Enforces tenant resource isolation for sellers (03_PHASE_PROMPTS.md Phase 3 Acceptance Criterion #2):
/// "A seller cannot read or modify another seller's resources."
/// </summary>
public sealed class SellerResourceIsolationHandler
    : AuthorizationHandler<SellerResourceIsolationRequirement, ISellerOwnedResource>
{
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        SellerResourceIsolationRequirement requirement,
        ISellerOwnedResource resource)
    {
        if (resource.SellerId == Guid.Empty)
        {
            return Task.CompletedTask;
        }

        // System admins can inspect across sellers for moderation/support
        bool isAdmin = context.User.Claims.Any(c =>
            c.Type == ClaimTypes.Role &&
            string.Equals(c.Value, Permissions.Roles.Admin, StringComparison.OrdinalIgnoreCase));

        if (isAdmin)
        {
            context.Succeed(requirement);
            return Task.CompletedTask;
        }

        string? userSellerIdClaim = context.User.FindFirstValue(JwtTokenService.SellerIdClaimType);
        if (Guid.TryParse(userSellerIdClaim, out Guid authenticatedSellerId) &&
            authenticatedSellerId == resource.SellerId)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
