using Microsoft.AspNetCore.Mvc.Filters;
using Mechware.Application.Identity;

namespace Mechware.Api.Authorization;

/// <summary>
/// Step-up authentication (brief §11.1, threat model v1 H3): the action runs only with a fresh authenticator code in the
/// <c>X-Step-Up-Code</c> header, checked against the signed-in staff member's own authenticator just before it. A stolen
/// session alone therefore cannot change credit, move money back or enrol someone's authenticator.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequireStepUpAttribute(string action) : Attribute, IAsyncActionFilter
{
    public const string HeaderName = "X-Step-Up-Code";

    public string Action { get; } = action;

    public async Task OnActionExecutionAsync(ActionExecutingContext context, ActionExecutionDelegate next)
    {
        var services = context.HttpContext.RequestServices;
        var currentUser = services.GetRequiredService<ICurrentUserService>();
        Guid userId = currentUser.UserId ?? throw new UnauthorizedAccessException("Authentication required.");
        string? code = context.HttpContext.Request.Headers[HeaderName].FirstOrDefault();
        // The ids in the route (company, refund, user) say what this code confirmed, for the audit log.
        string target = string.Join(",", context.RouteData.Values
            .Where(v => v.Key.EndsWith("Id", StringComparison.Ordinal))
            .Select(v => $"{v.Key}={v.Value}"));

        await services.GetRequiredService<IIdentityService>()
            .VerifyStepUpAsync(userId, code, Action, target, currentUser.IpAddress, cancellationToken: context.HttpContext.RequestAborted);
        await next();
    }
}
