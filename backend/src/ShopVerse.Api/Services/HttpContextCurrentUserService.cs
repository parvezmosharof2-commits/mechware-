using System.Security.Claims;
using ShopVerse.Application.Identity;
using ShopVerse.Infrastructure.Identity;

namespace ShopVerse.Api.Services;

public sealed class HttpContextCurrentUserService : ICurrentUserService
{
    private readonly IHttpContextAccessor _httpContextAccessor;

    public HttpContextCurrentUserService(IHttpContextAccessor httpContextAccessor)
    {
        _httpContextAccessor = httpContextAccessor;
    }

    public Guid? UserId
    {
        get
        {
            string? sub = _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.NameIdentifier)
                ?? _httpContextAccessor.HttpContext?.User.FindFirstValue("sub");
            return Guid.TryParse(sub, out Guid id) ? id : null;
        }
    }

    public string? Email =>
        _httpContextAccessor.HttpContext?.User.FindFirstValue(ClaimTypes.Email);

    public Guid? SellerId
    {
        get
        {
            string? sellerClaim = _httpContextAccessor.HttpContext?.User.FindFirstValue(JwtTokenService.SellerIdClaimType);
            return Guid.TryParse(sellerClaim, out Guid id) ? id : null;
        }
    }

    public string? IpAddress =>
        _httpContextAccessor.HttpContext?.Connection.RemoteIpAddress?.ToString();

    public string? UserAgent =>
        _httpContextAccessor.HttpContext?.Request.Headers.UserAgent.ToString();

    public string? CorrelationId =>
        _httpContextAccessor.HttpContext?.Response.Headers["X-Correlation-Id"].FirstOrDefault()
        ?? _httpContextAccessor.HttpContext?.TraceIdentifier;

    public bool HasPermission(string permission) =>
        _httpContextAccessor.HttpContext?.User.Claims.Any(c =>
            string.Equals(c.Type, JwtTokenService.PermissionClaimType, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(c.Value, permission, StringComparison.OrdinalIgnoreCase)) ?? false;
}
