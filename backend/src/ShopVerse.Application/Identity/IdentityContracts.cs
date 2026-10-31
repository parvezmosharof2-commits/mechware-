using System.Security.Claims;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Application.Identity;

/// <summary>
/// Marker interface for any domain or application resource owned by a specific Seller.
/// Used by SellerResourceIsolationHandler to enforce strict tenant isolation.
/// </summary>
public interface ISellerOwnedResource
{
    Guid SellerId { get; }
}

public sealed record SellerOwnedResource(Guid ResourceId, Guid SellerId, string ResourceType) : ISellerOwnedResource;

public sealed record UpdateSellerResourceRequestDto(
    string Sku,
    decimal UnitPriceBdt,
    int StockQuantity);

public sealed record RegisterRequestDto(
    string Email,
    string? Phone,
    string Password,
    string FullName,
    string Locale = "en");

public sealed record RegisterResponseDto(
    Guid UserId,
    string Status,
    string VerificationChannel,
    string ReferenceId,
    int ExpiresInSeconds);

public sealed record SendOtpRequestDto(
    string Recipient,
    string Type = "PhoneVerify");

public sealed record SendOtpResponseDto(
    string Recipient,
    string Type,
    string ReferenceId,
    int ExpiresInSeconds,
    int MaxAttempts = 3);

public sealed record VerifyOtpRequestDto(
    string Recipient,
    string Code,
    string Type,
    string ReferenceId);

public sealed record LoginRequestDto(
    string Identifier,
    string Password,
    string? TotpCode = null);

public sealed record AuthenticatedUserDto(
    Guid Id,
    string Email,
    string? Phone,
    string FullName,
    IReadOnlyList<string> Roles,
    IReadOnlyList<string> Permissions,
    string Locale,
    bool TwoFactorEnabled,
    Guid? SellerId = null);

public sealed record AuthTokenResponseDto(
    string AccessToken,
    int ExpiresIn,
    string TokenType,
    AuthenticatedUserDto User,
    string? RefreshTokenCookieValue = null,
    bool RequiresTwoFactor = false,
    string? TwoFactorChallengeToken = null);

public sealed record GoogleLoginRequestDto(
    string IdToken,
    string? ExpectedEmail = null,
    string? FullName = null);

public sealed record ForgotPasswordRequestDto(string Identifier);

public sealed record ResetPasswordRequestDto(
    string Recipient,
    string ReferenceId,
    string OtpCode,
    string NewPassword);

public sealed record TotpSetupResponseDto(
    string Secret,
    string OtpAuthUri,
    IReadOnlyList<string> RecoveryCodes);

public sealed record TotpVerifyRequestDto(string Code);

public sealed record ActiveSessionDto(
    Guid SessionId,
    Guid FamilyId,
    string? IpAddress,
    string? UserAgent,
    DateTime CreatedAtUtc,
    DateTime ExpiresAtUtc,
    bool IsCurrent);

public sealed record RevokeSessionsRequestDto(
    Guid? SessionId = null,
    bool RevokeAll = true,
    bool RevokeAllExceptCurrent = false);

public sealed record UpsertAddressRequestDto(
    string Label,
    string RecipientName,
    string Phone,
    string Line1,
    string? Line2,
    string? Area,
    string Division,
    string District,
    string Upazila,
    string? PostalCode,
    bool IsDefaultShipping = false,
    bool IsDefaultBilling = false,
    decimal? Latitude = null,
    decimal? Longitude = null);

public sealed record AddressDto(
    Guid Id,
    string Label,
    string RecipientName,
    string Phone,
    string Line1,
    string? Line2,
    string? Area,
    string Division,
    string District,
    string Upazila,
    string? PostalCode,
    string Country,
    bool IsDefaultShipping,
    bool IsDefaultBilling,
    decimal? Latitude,
    decimal? Longitude);

public sealed record BdLocationDto(
    Guid Id,
    Guid? ParentId,
    string Level,
    string NameEn,
    string NameBn,
    string ShippingZone,
    string? PostalCodePrefix);

public sealed record GdprExportBundleDto(
    DateTime ExportedAtUtc,
    AuthenticatedUserDto Profile,
    IReadOnlyList<AddressDto> Addresses,
    IReadOnlyList<ActiveSessionDto> ActiveSessions,
    IReadOnlyList<AuditLogSummaryDto> RecentAuditEvents);

public sealed record AuditLogSummaryDto(
    Guid Id,
    string Action,
    string EntityName,
    string EntityId,
    string? ActorIp,
    DateTime CreatedAtUtc);

public interface ISmsProvider
{
    Task SendSmsAsync(string phoneNumber, string message, CancellationToken cancellationToken = default);
}

public interface IPasswordHasherService
{
    void ValidatePasswordPolicy(string password);
    string HashPassword(string password);
    bool VerifyPassword(string password, string passwordHash);
}

public interface ITotpService
{
    string GenerateBase32Secret(int byteLength = 20);
    string BuildOtpAuthUri(string email, string base32Secret, string issuer = "MECHWARE");
    string ComputeTotpCode(string base32Secret, DateTimeOffset timestamp);
    bool VerifyTotpCode(string base32Secret, string code, DateTimeOffset timestamp, int windowSteps = 1);
    bool TryVerifyTotpCode(
        string base32Secret,
        string code,
        DateTimeOffset timestamp,
        long? lastUsedTimeStep,
        out long matchedTimeStep,
        int windowSteps = 1);
    IReadOnlyList<string> GenerateRecoveryCodes(int count = 10);
    string HashRecoveryCode(string rawCode);
}

public interface ITokenService
{
    string GenerateAccessToken(
        User user,
        IReadOnlyList<string> roles,
        IReadOnlyList<string> permissions,
        DateTime utcNow);

    ClaimsPrincipal? ValidateAccessToken(string token, DateTime utcNow);
    string GenerateRawRefreshToken();
    string HashToken(string rawToken);
}

public interface ICurrentUserService
{
    Guid? UserId { get; }
    string? Email { get; }
    Guid? SellerId { get; }
    string? IpAddress { get; }
    string? UserAgent { get; }
    string? CorrelationId { get; }
    bool HasPermission(string permission);
}

/// <summary>
/// Application-layer Identity & Access boundary interface (AUD-P3-03 Clean Architecture DIP).
/// </summary>
public interface IIdentityService
{
    Task<(RegisterResponseDto Response, string RawOtpForDev)> RegisterAsync(
        RegisterRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default);

    Task<(SendOtpResponseDto Response, string RawOtpForDev)> SendOtpAsync(
        SendOtpRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default);

    Task<AuthTokenResponseDto> VerifyOtpAsync(
        VerifyOtpRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default);

    Task<AuthTokenResponseDto> LoginAsync(
        LoginRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default);

    Task<AuthTokenResponseDto> GoogleLoginAsync(
        GoogleLoginRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default);

    Task<AuthTokenResponseDto> RotateRefreshTokenAsync(
        string rawRefreshToken,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default);

    Task<bool> RevokeRefreshTokenByCookieAsync(
        string? rawRefreshToken,
        string? clientIp = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default);

    Task<(string ReferenceId, string RawOtpForDev)> InitiatePasswordResetAsync(
        string identifier,
        string? clientIp = null,
        CancellationToken cancellationToken = default);

    Task CompletePasswordResetAsync(
        ResetPasswordRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default);

    Task<TotpSetupResponseDto> SetupTwoFactorAsync(Guid userId, string? clientIp = null, CancellationToken cancellationToken = default);

    Task VerifyAndEnableTwoFactorAsync(
        Guid userId,
        string code,
        string? clientIp = null,
        DateTimeOffset? timestampOverride = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ActiveSessionDto>> GetActiveSessionsAsync(
        Guid userId,
        string? currentRawRefreshToken = null,
        CancellationToken cancellationToken = default);

    Task<int> RevokeSessionsAsync(
        Guid userId,
        RevokeSessionsRequestDto request,
        string? clientIp = null,
        string? currentRawRefreshToken = null,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AddressDto>> GetAddressesAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<AddressDto> CreateAddressAsync(Guid userId, UpsertAddressRequestDto request, string? clientIp = null, CancellationToken cancellationToken = default);

    Task<AddressDto> UpdateAddressAsync(Guid userId, Guid addressId, UpsertAddressRequestDto request, string? clientIp = null, CancellationToken cancellationToken = default);

    Task DeleteAddressAsync(Guid userId, Guid addressId, string? clientIp = null, CancellationToken cancellationToken = default);

    IReadOnlyList<BdLocationDto> GetBdLocations(string? level = null, Guid? parentId = null);

    Task<GdprExportBundleDto> ExportUserDataAsync(Guid userId, string? clientIp = null, CancellationToken cancellationToken = default);

    Task AnonymizeAndEraseUserAsync(Guid userId, string? clientIp = null, CancellationToken cancellationToken = default);

    Task<AuthenticatedUserDto> GetUserProfileAsync(Guid userId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<User>> GetUsersForAuditAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<AuditLog>> GetAuditLogsAsync(CancellationToken cancellationToken = default);
}
