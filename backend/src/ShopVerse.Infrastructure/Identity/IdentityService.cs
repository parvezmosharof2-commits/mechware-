using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Identity;
using ShopVerse.Infrastructure.Persistence;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// Core Identity, Authentication, Token Rotation/Family Breach Detection, TOTP 2FA with Replay Guard,
/// Address Book, Bangladesh Location Hierarchy, and GDPR Export/Erasure service. State lives in PostgreSQL, so it
/// survives restarts and is shared by every API instance. Registered per request (it holds the request's DbContext).
/// </summary>
public sealed class IdentityService : IIdentityService
{
    public static readonly Guid SellerAlphaId = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    public static readonly Guid SellerBetaId = Guid.Parse("bbbb0000-0000-0000-0000-000000000002");

    private readonly IPasswordHasherService _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ITotpService _totpService;
    private readonly ISmsProvider _smsProvider;
    private readonly ShopVerseDbContext _db;
    private readonly IReadOnlyList<BdLocation> _bdLocations;

    public IdentityService(
        IPasswordHasherService passwordHasher,
        ITokenService tokenService,
        ITotpService totpService,
        ISmsProvider smsProvider,
        ShopVerseDbContext db)
    {
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _totpService = totpService;
        _smsProvider = smsProvider;
        _db = db;
        _bdLocations = BangladeshLocationSeeder.GetSeedLocations();
    }

    public async Task<(RegisterResponseDto Response, string RawOtpForDev)> RegisterAsync(
        RegisterRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        _passwordHasher.ValidatePasswordPolicy(request.Password);

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        if (await _db.Users.AnyAsync(u => u.DeletedAtUtc == null && u.Email == normalizedEmail, cancellationToken))
        {
            throw new ValidationException("email", "An account with this email address already exists.");
        }

        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            string normalizedPhone = request.Phone.Trim();
            if (await _db.Users.AnyAsync(u => u.DeletedAtUtc == null && u.Phone == normalizedPhone, cancellationToken))
            {
                throw new ValidationException("phone", "An account with this phone number already exists.");
            }
        }

        string passwordHash = _passwordHasher.HashPassword(request.Password);
        var user = User.Create(normalizedEmail, request.Phone?.Trim(), passwordHash, request.FullName, request.Locale);
        user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
        _db.Users.Add(user);

        bool verifyByPhone = !string.IsNullOrWhiteSpace(user.Phone);
        string recipient = verifyByPhone ? user.Phone! : user.Email;
        OtpType otpType = verifyByPhone ? OtpType.PhoneVerify : OtpType.EmailVerify;

        (OtpCode otp, string rawCode) = CreateOtp(recipient, otpType, now);
        RecordAudit(user.Id, clientIp, "user.registered", "users", user.Id.ToString(), null, new { user.Email, user.Phone, user.Locale });

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two requests raced past the checks above; the unique index is the real guard.
            throw new ValidationException("email", "An account with this email address or phone number already exists.");
        }

        if (verifyByPhone)
        {
            await _smsProvider.SendSmsAsync(
                recipient,
                $"MECHWARE Verification Code: {rawCode}. Valid for 5 minutes. Ref: {otp.ReferenceId}",
                cancellationToken);
        }

        var response = new RegisterResponseDto(
            user.Id,
            "pending_verification",
            verifyByPhone ? "phone_otp" : "email_otp",
            otp.ReferenceId,
            300);

        return (response, rawCode);
    }

    public async Task<(SendOtpResponseDto Response, string RawOtpForDev)> SendOtpAsync(
        SendOtpRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(request.Recipient))
        {
            throw new ValidationException("recipient", "OTP recipient phone or email is required.");
        }

        var now = DateTime.UtcNow;
        string recipient = request.Recipient.Trim().ToLowerInvariant();
        if (!Enum.TryParse<OtpType>(request.Type, ignoreCase: true, out OtpType otpType))
        {
            otpType = OtpType.PhoneVerify;
        }

        (OtpCode otp, string rawCode) = CreateOtp(recipient, otpType, now);

        User? user = await FindUserByIdentifierAsync(recipient, cancellationToken);
        RecordAudit(user?.Id, clientIp, "user.otp_dispatched", "otp_codes", otp.Id.ToString(), null, new { recipient, otp.Type, otp.ReferenceId });
        await _db.SaveChangesAsync(cancellationToken);

        if (recipient.StartsWith('+') || recipient.All(char.IsDigit))
        {
            await _smsProvider.SendSmsAsync(
                recipient,
                $"MECHWARE Security Code: {rawCode}. Valid for 5 minutes. Ref: {otp.ReferenceId}",
                cancellationToken);
        }

        return (new SendOtpResponseDto(recipient, otpType.ToString(), otp.ReferenceId, 300, OtpCode.MaxVerificationAttempts), rawCode);
    }

    public async Task<AuthTokenResponseDto> VerifyOtpAsync(
        VerifyOtpRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        string recipient = request.Recipient.Trim().ToLowerInvariant();

        // The attempt counter is the brute-force limit, so the row is locked while it is checked and updated.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        OtpCode? otp = await LockOtpAsync(recipient, request.ReferenceId, null, cancellationToken);

        if (otp is null || !otp.CanVerify(now))
        {
            throw new ValidationException("code", "Maximum OTP verification attempts exceeded, or OTP has expired / been consumed.");
        }

        string providedHash = _tokenService.HashToken(request.Code);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(otp.CodeHash),
            Encoding.ASCII.GetBytes(providedHash)))
        {
            otp.IncrementAttempts(now);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            throw new ValidationException("code", "Invalid verification code.");
        }

        otp.MarkConsumed(now);

        User user = await FindUserByIdentifierAsync(recipient, cancellationToken)
            ?? throw new KeyNotFoundException("User account for OTP recipient was not found.");

        if (otp.Type == OtpType.PhoneVerify)
        {
            user.VerifyPhone(now);
        }
        else
        {
            user.VerifyEmail(now);
        }

        user.RecordSuccessfulLogin(now);
        RecordAudit(user.Id, clientIp, "user.otp_verified", "users", user.Id.ToString(), null, new { otp.Type, otp.ReferenceId });

        AuthTokenResponseDto response = IssueSessionTokens(user, Guid.NewGuid(), clientIp, userAgent, now);
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return response;
    }

    public async Task<AuthTokenResponseDto> LoginAsync(
        LoginRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        User? user = await FindUserByIdentifierAsync(request.Identifier, cancellationToken);

        if (user is null || user.DeletedAtUtc.HasValue || string.IsNullOrEmpty(user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        if (user.IsLockedOut(now))
        {
            RecordAudit(user.Id, clientIp, "user.login_locked_out", "users", user.Id.ToString(), null, new { user.LockoutUntilUtc });
            await _db.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException($"Account is temporarily locked due to multiple failed login attempts until {user.LockoutUntilUtc:O}.");
        }

        if (!_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin(now);
            RecordAudit(user.Id, clientIp, "user.login_failed", "users", user.Id.ToString(), null, new { user.FailedLoginAttempts, user.LockoutUntilUtc });
            await _db.SaveChangesAsync(cancellationToken);
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        IReadOnlyList<string> roles = GetUserRoles(user);
        bool requiresMandatory2Fa = roles.Any(Permissions.Roles.RequiresMandatoryTwoFactor);

        if (user.TwoFactorEnabled || requiresMandatory2Fa)
        {
            if (string.IsNullOrWhiteSpace(request.TotpCode))
            {
                return new AuthTokenResponseDto(
                    string.Empty,
                    0,
                    "Bearer",
                    MapUserDto(user),
                    null,
                    RequiresTwoFactor: true,
                    TwoFactorChallengeToken: $"mfa_challenge_{user.Id:N}");
            }

            if (!VerifyUserTwoFactorOrRecoveryCode(user, request.TotpCode, now))
            {
                user.RecordFailedLogin(now);
                RecordAudit(user.Id, clientIp, "user.2fa_failed", "users", user.Id.ToString(), null, null);
                await _db.SaveChangesAsync(cancellationToken);
                throw new UnauthorizedAccessException("Invalid or replayed two-factor authentication / recovery code.");
            }
        }

        user.RecordSuccessfulLogin(now);
        RecordAudit(user.Id, clientIp, "user.login_succeeded", "users", user.Id.ToString(), null, new { clientIp });

        AuthTokenResponseDto response = IssueSessionTokens(user, Guid.NewGuid(), clientIp, userAgent, now);
        await _db.SaveChangesAsync(cancellationToken);
        return response;
    }

    public async Task<AuthTokenResponseDto> GoogleLoginAsync(
        GoogleLoginRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length < 10)
        {
            throw new ValidationException("idToken", "Valid Google OIDC ID token is required.");
        }

        (string subjectId, string email, string fullName) = ValidateAndExtractGoogleOidcClaims(request, now);

        User? user = await _db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(
                u => u.DeletedAtUtc == null && (u.GoogleSubjectId == subjectId || u.Email == email),
                cancellationToken);

        if (user is null)
        {
            user = User.Create(email, null, null, fullName, "en");
            user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
            _db.Users.Add(user);
        }

        user.LinkGoogleAccount(subjectId, now);
        user.RecordSuccessfulLogin(now);

        RecordAudit(user.Id, clientIp, "user.google_oauth_login", "users", user.Id.ToString(), null, new { email, subjectId });

        AuthTokenResponseDto response = IssueSessionTokens(user, Guid.NewGuid(), clientIp, userAgent, now);
        await _db.SaveChangesAsync(cancellationToken);
        return response;
    }

    /// <summary>
    /// Rotates a refresh token. Implements ADR-0006:
    /// - 10-second multi-tab/SSR race grace window from same IP returns valid access token without revoking family.
    /// - Any reuse outside the grace window or from a different IP triggers immediate revocation of the entire token family.
    /// The token row is locked (SELECT ... FOR UPDATE) so two concurrent rotations of one token are serialised: exactly
    /// one wins, the other is seen as a reuse.
    /// </summary>
    public async Task<AuthTokenResponseDto> RotateRefreshTokenAsync(
        string rawRefreshToken,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw new UnauthorizedAccessException("Refresh token is missing.");
        }

        var now = utcNowOverride ?? DateTime.UtcNow;
        string tokenHash = _tokenService.HashToken(rawRefreshToken);

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        RefreshToken? storedToken = (await _db.RefreshTokens
            .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE token_hash = {tokenHash} FOR UPDATE")
            .ToListAsync(cancellationToken)).FirstOrDefault();

        if (storedToken is null)
        {
            throw new UnauthorizedAccessException("Invalid refresh token.");
        }

        User? user = await _db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == storedToken.UserId, cancellationToken);
        if (user is null || user.DeletedAtUtc.HasValue || user.Status == UserStatus.Banned)
        {
            throw new UnauthorizedAccessException("User account is no longer active.");
        }

        if (storedToken.IsUsed)
        {
            // ADR-0006 10-second multi-tab/SSR grace window
            if (storedToken.IsWithinGraceWindow(now, clientIp, graceSeconds: 10))
            {
                IReadOnlyList<string> roles = GetUserRoles(user);
                IReadOnlyList<string> permissions = GetUserPermissions(roles);
                string accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, now);

                return new AuthTokenResponseDto(
                    accessToken,
                    900,
                    "Bearer",
                    MapUserDto(user),
                    RefreshTokenCookieValue: null);
            }

            // BREACH DETECTED: token reuse outside the grace window or from a different IP. Revoke the whole family
            // and persist that before the request fails, otherwise the rollback would undo the protection.
            await RevokeTokenFamilyAsync(storedToken.FamilyId, now, cancellationToken);
            RecordAudit(
                user.Id,
                clientIp,
                "security.refresh_token_family_revoked_on_reuse",
                "refresh_tokens",
                storedToken.FamilyId.ToString(),
                new { storedToken.CreatedByIp, storedToken.UsedAtUtc },
                new { BreachIp = clientIp, DetectedAtUtc = now });
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);

            throw new UnauthorizedAccessException("Refresh token reuse detected. Entire session family has been revoked for security.");
        }

        if (storedToken.IsRevoked || storedToken.IsExpired(now))
        {
            throw new UnauthorizedAccessException("Refresh token is expired or revoked.");
        }

        string newRawRefresh = _tokenService.GenerateRawRefreshToken();
        string newRefreshHash = _tokenService.HashToken(newRawRefresh);

        storedToken.MarkUsed(newRefreshHash, now);

        _db.RefreshTokens.Add(new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            newRefreshHash,
            storedToken.FamilyId,
            clientIp,
            userAgent,
            now.AddDays(7)));

        IReadOnlyList<string> userRoles = GetUserRoles(user);
        IReadOnlyList<string> userPermissions = GetUserPermissions(userRoles);
        string newAccessToken = _tokenService.GenerateAccessToken(user, userRoles, userPermissions, now);

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new AuthTokenResponseDto(
            newAccessToken,
            900,
            "Bearer",
            MapUserDto(user),
            RefreshTokenCookieValue: newRawRefresh);
    }

    /// <summary>
    /// Revokes the specific refresh token family associated with the caller's httpOnly refresh_token cookie
    /// (AUD-P3-05: supports logout even when the 15-minute Access JWT has expired, without wiping other device sessions).
    /// </summary>
    public async Task<bool> RevokeRefreshTokenByCookieAsync(
        string? rawRefreshToken,
        string? clientIp = null,
        DateTime? utcNowOverride = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return false;
        }

        var now = utcNowOverride ?? DateTime.UtcNow;
        string tokenHash = _tokenService.HashToken(rawRefreshToken);

        RefreshToken? storedToken = await _db.RefreshTokens.FirstOrDefaultAsync(rt => rt.TokenHash == tokenHash, cancellationToken);
        if (storedToken is null)
        {
            return false;
        }

        await RevokeTokenFamilyAsync(storedToken.FamilyId, now, cancellationToken);
        RecordAudit(
            storedToken.UserId,
            clientIp,
            "user.session_logged_out",
            "refresh_tokens",
            storedToken.FamilyId.ToString(),
            null,
            new { storedToken.FamilyId });
        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<(string ReferenceId, string RawOtpForDev)> InitiatePasswordResetAsync(
        string identifier,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        User? user = await FindUserByIdentifierAsync(identifier, cancellationToken);
        string recipient = user?.Email ?? identifier.Trim().ToLowerInvariant();

        (OtpCode otp, string rawCode) = CreateOtp(recipient, OtpType.PasswordReset, now);

        if (user is not null)
        {
            RecordAudit(user.Id, clientIp, "user.password_reset_requested", "users", user.Id.ToString(), null, new { otp.ReferenceId });
        }

        await _db.SaveChangesAsync(cancellationToken);
        return (otp.ReferenceId, rawCode);
    }

    public async Task CompletePasswordResetAsync(
        ResetPasswordRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        _passwordHasher.ValidatePasswordPolicy(request.NewPassword);

        string recipient = request.Recipient.Trim().ToLowerInvariant();

        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        OtpCode? otp = await LockOtpAsync(recipient, request.ReferenceId, OtpType.PasswordReset, cancellationToken);

        if (otp is null || !otp.CanVerify(now))
        {
            throw new ValidationException("otpCode", "Password reset code is invalid or expired.");
        }

        string codeHash = _tokenService.HashToken(request.OtpCode);
        if (!CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(otp.CodeHash),
            Encoding.ASCII.GetBytes(codeHash)))
        {
            otp.IncrementAttempts(now);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            throw new ValidationException("otpCode", "Invalid password reset code.");
        }

        otp.MarkConsumed(now);

        User user = await FindUserByIdentifierAsync(recipient, cancellationToken)
            ?? throw new KeyNotFoundException("User account was not found.");

        user.UpdatePasswordHash(_passwordHasher.HashPassword(request.NewPassword), now);

        // A password change ends every existing session.
        await RevokeAllUserSessionsAsync(user.Id, now, cancellationToken);
        RecordAudit(user.Id, clientIp, "user.password_reset_completed", "users", user.Id.ToString(), null, null);

        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
    }

    public async Task<TotpSetupResponseDto> SetupTwoFactorAsync(
        Guid userId,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        User user = await GetUserByIdOrThrowAsync(userId, cancellationToken);

        string secret = _totpService.GenerateBase32Secret();
        IReadOnlyList<string> rawRecoveryCodes = _totpService.GenerateRecoveryCodes(10);
        string hashedCodesJson = JsonSerializer.Serialize(rawRecoveryCodes.Select(_totpService.HashRecoveryCode).ToList());

        user.ConfigureTwoFactorSecret(secret, hashedCodesJson, now);
        string otpAuthUri = _totpService.BuildOtpAuthUri(user.Email, secret);

        RecordAudit(user.Id, clientIp, "user.2fa_setup_initiated", "users", user.Id.ToString(), null, null);
        await _db.SaveChangesAsync(cancellationToken);

        return new TotpSetupResponseDto(secret, otpAuthUri, rawRecoveryCodes);
    }

    public async Task VerifyAndEnableTwoFactorAsync(
        Guid userId,
        string code,
        string? clientIp = null,
        DateTimeOffset? timestampOverride = null,
        CancellationToken cancellationToken = default)
    {
        var now = timestampOverride ?? DateTimeOffset.UtcNow;
        User user = await GetUserByIdOrThrowAsync(userId, cancellationToken);

        if (string.IsNullOrWhiteSpace(user.TwoFactorSecret))
        {
            throw new ValidationException("code", "2FA setup has not been initiated for this user.");
        }

        if (!_totpService.TryVerifyTotpCode(user.TwoFactorSecret, code, now, user.LastUsedTotpTimeStep, out long matchedStep))
        {
            throw new ValidationException("code", "Invalid or already-used 6-digit TOTP authenticator code.");
        }

        user.RecordTotpTimeStepUsed(matchedStep, now.UtcDateTime);
        user.EnableTwoFactor(now.UtcDateTime);
        RecordAudit(user.Id, clientIp, "user.2fa_enabled", "users", user.Id.ToString(), null, new { TwoFactorEnabled = true, MatchedTimeStep = matchedStep });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<ActiveSessionDto>> GetActiveSessionsAsync(
        Guid userId,
        string? currentRawRefreshToken = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        string? currentHash = string.IsNullOrWhiteSpace(currentRawRefreshToken)
            ? null
            : _tokenService.HashToken(currentRawRefreshToken);

        List<RefreshToken> tokens = await _db.RefreshTokens
            .AsNoTracking()
            .Where(rt => rt.UserId == userId && !rt.IsUsed && !rt.IsRevoked && rt.ExpiresAtUtc > now)
            .OrderByDescending(rt => rt.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return tokens
            .Select(rt => new ActiveSessionDto(
                rt.Id,
                rt.FamilyId,
                rt.CreatedByIp,
                rt.UserAgent,
                rt.CreatedAtUtc,
                rt.ExpiresAtUtc,
                currentHash is not null && string.Equals(rt.TokenHash, currentHash, StringComparison.Ordinal)))
            .ToList()
            .AsReadOnly();
    }

    public async Task<int> RevokeSessionsAsync(
        Guid userId,
        RevokeSessionsRequestDto request,
        string? clientIp = null,
        string? currentRawRefreshToken = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        string? currentHash = string.IsNullOrWhiteSpace(currentRawRefreshToken)
            ? null
            : _tokenService.HashToken(currentRawRefreshToken);

        Guid? currentFamilyId = currentHash is null
            ? null
            : await _db.RefreshTokens
                .Where(rt => rt.UserId == userId && rt.TokenHash == currentHash)
                .Select(rt => (Guid?)rt.FamilyId)
                .FirstOrDefaultAsync(cancellationToken);

        IQueryable<RefreshToken> targets = _db.RefreshTokens.Where(rt => rt.UserId == userId && !rt.IsRevoked);

        if (request.SessionId.HasValue)
        {
            Guid sessionId = request.SessionId.Value;
            targets = targets.Where(rt => rt.Id == sessionId || rt.FamilyId == sessionId);
        }
        else if (request.RevokeAllExceptCurrent && currentFamilyId.HasValue)
        {
            Guid family = currentFamilyId.Value;
            targets = targets.Where(rt => rt.FamilyId != family);
        }
        else if (!request.RevokeAll && currentFamilyId.HasValue)
        {
            Guid family = currentFamilyId.Value;
            targets = targets.Where(rt => rt.FamilyId == family);
        }

        List<RefreshToken> tokens = await targets.ToListAsync(cancellationToken);
        foreach (RefreshToken token in tokens)
        {
            token.Revoke(now);
        }

        int revokedCount = tokens.Count;
        RecordAudit(userId, clientIp, "user.sessions_revoked", "refresh_tokens", userId.ToString(), null, new { revokedCount, request.RevokeAll, request.RevokeAllExceptCurrent });
        await _db.SaveChangesAsync(cancellationToken);
        return revokedCount;
    }

    public async Task<IReadOnlyList<AddressDto>> GetAddressesAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        List<Address> addresses = await _db.Addresses
            .AsNoTracking()
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsDefaultShipping)
            .ThenByDescending(a => a.CreatedAtUtc)
            .ToListAsync(cancellationToken);

        return addresses.Select(MapAddressDto).ToList().AsReadOnly();
    }

    public async Task<AddressDto> CreateAddressAsync(
        Guid userId,
        UpsertAddressRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        ValidateBdLocationHierarchy(request.Division, request.District, request.Upazila);

        if (request.IsDefaultShipping || request.IsDefaultBilling)
        {
            await ClearConflictingDefaultAddressesAsync(userId, request.IsDefaultShipping, request.IsDefaultBilling, now, null, cancellationToken);
        }

        var address = new Address(
            Guid.NewGuid(),
            userId,
            request.Label,
            request.RecipientName,
            request.Phone,
            request.Line1,
            request.Line2,
            request.Area,
            request.Upazila,
            request.District,
            request.Division,
            request.PostalCode,
            request.IsDefaultShipping,
            request.IsDefaultBilling,
            request.Latitude,
            request.Longitude);

        _db.Addresses.Add(address);
        RecordAudit(userId, clientIp, "address.created", "addresses", address.Id.ToString(), null, new { address.Division, address.District, address.Upazila });
        await _db.SaveChangesAsync(cancellationToken);

        return MapAddressDto(address);
    }

    public async Task<AddressDto> UpdateAddressAsync(
        Guid userId,
        Guid addressId,
        UpsertAddressRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        Address address = await _db.Addresses.FirstOrDefaultAsync(a => a.Id == addressId && a.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException("Address not found.");

        ValidateBdLocationHierarchy(request.Division, request.District, request.Upazila);

        if (request.IsDefaultShipping || request.IsDefaultBilling)
        {
            await ClearConflictingDefaultAddressesAsync(userId, request.IsDefaultShipping, request.IsDefaultBilling, now, addressId, cancellationToken);
        }

        address.Update(
            request.Label,
            request.RecipientName,
            request.Phone,
            request.Line1,
            request.Line2,
            request.Area,
            request.Upazila,
            request.District,
            request.Division,
            request.PostalCode,
            request.IsDefaultShipping,
            request.IsDefaultBilling,
            request.Latitude,
            request.Longitude,
            now);

        RecordAudit(userId, clientIp, "address.updated", "addresses", address.Id.ToString(), null, new { address.Division, address.District, address.Upazila });
        await _db.SaveChangesAsync(cancellationToken);

        return MapAddressDto(address);
    }

    public async Task DeleteAddressAsync(
        Guid userId,
        Guid addressId,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        Address address = await _db.Addresses.FirstOrDefaultAsync(a => a.Id == addressId && a.UserId == userId, cancellationToken)
            ?? throw new KeyNotFoundException("Address not found.");

        _db.Addresses.Remove(address);
        RecordAudit(userId, clientIp, "address.deleted", "addresses", addressId.ToString(), new { address.Label, address.Line1 }, null);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public IReadOnlyList<BdLocationDto> GetBdLocations(string? level = null, Guid? parentId = null)
    {
        IEnumerable<BdLocation> query = _bdLocations;

        if (!string.IsNullOrWhiteSpace(level))
        {
            query = query.Where(l => string.Equals(l.Level, level.Trim(), StringComparison.OrdinalIgnoreCase));
        }

        if (parentId.HasValue)
        {
            query = query.Where(l => l.ParentId == parentId.Value);
        }

        return query
            .Select(l => new BdLocationDto(l.Id, l.ParentId, l.Level, l.NameEn, l.NameBn, l.ShippingZone, l.PostalCodePrefix))
            .ToList()
            .AsReadOnly();
    }

    public async Task<GdprExportBundleDto> ExportUserDataAsync(
        Guid userId,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        User user = await GetUserByIdOrThrowAsync(userId, cancellationToken);
        RecordAudit(userId, clientIp, "user.gdpr_export", "users", userId.ToString(), null, null);
        await _db.SaveChangesAsync(cancellationToken);

        List<AuditLog> events = await _db.AuditLogs
            .AsNoTracking()
            .Where(a => a.ActorId == userId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(100)
            .ToListAsync(cancellationToken);

        IReadOnlyList<AuditLogSummaryDto> auditEvents = events
            .Select(a => new AuditLogSummaryDto(a.Id, a.Action, a.EntityName, a.EntityId, a.ActorIp, a.CreatedAtUtc))
            .ToList()
            .AsReadOnly();

        return new GdprExportBundleDto(
            DateTime.UtcNow,
            MapUserDto(user),
            await GetAddressesAsync(userId, cancellationToken),
            await GetActiveSessionsAsync(userId, null, cancellationToken),
            auditEvents);
    }

    public async Task AnonymizeAndEraseUserAsync(
        Guid userId,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        User user = await GetUserByIdOrThrowAsync(userId, cancellationToken);

        user.AnonymizeForGdpr(now);
        await RevokeAllUserSessionsAsync(userId, now, cancellationToken);
        _db.Addresses.RemoveRange(await _db.Addresses.Where(a => a.UserId == userId).ToListAsync(cancellationToken));

        RecordAudit(
            userId,
            clientIp,
            "user.gdpr_anonymized",
            "users",
            userId.ToString(),
            new { EmailRedacted = true, PreviousStatus = "Active" },
            new { user.Email, Status = user.Status.ToString() });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<AuthenticatedUserDto> GetUserProfileAsync(Guid userId, CancellationToken cancellationToken = default)
    {
        User user = await GetUserByIdOrThrowAsync(userId, cancellationToken);
        return MapUserDto(user);
    }

    public async Task<IReadOnlyList<User>> GetUsersForAuditAsync(CancellationToken cancellationToken = default) =>
        await _db.Users.AsNoTracking().Include(u => u.UserRoles).ToListAsync(cancellationToken);

    public async Task<IReadOnlyList<AuditLog>> GetAuditLogsAsync(CancellationToken cancellationToken = default) =>
        await _db.AuditLogs.AsNoTracking().OrderBy(a => a.CreatedAtUtc).ToListAsync(cancellationToken);

    private (string SubjectId, string Email, string FullName) ValidateAndExtractGoogleOidcClaims(
        GoogleLoginRequestDto request,
        DateTime utcNow)
    {
        string rawToken = request.IdToken.Trim();
        string[] segments = rawToken.Split('.');

        if (segments.Length == 3)
        {
            try
            {
                string payloadJson = Encoding.UTF8.GetString(FromBase64Url(segments[1]));
                using JsonDocument doc = JsonDocument.Parse(payloadJson);
                JsonElement root = doc.RootElement;

                string? iss = root.TryGetProperty("iss", out JsonElement issEl) ? issEl.GetString() : null;
                if (iss is not ("https://accounts.google.com" or "accounts.google.com"))
                {
                    throw new ValidationException("idToken", "Invalid Google OIDC token issuer (iss).");
                }

                if (root.TryGetProperty("exp", out JsonElement expEl) && expEl.TryGetInt64(out long expUnix))
                {
                    DateTime expUtc = DateTimeOffset.FromUnixTimeSeconds(expUnix).UtcDateTime;
                    if (expUtc <= utcNow)
                    {
                        throw new UnauthorizedAccessException("Google OIDC ID token has expired.");
                    }
                }
                else
                {
                    throw new ValidationException("idToken", "Google OIDC ID token is missing required expiration (exp) claim.");
                }

                if (root.TryGetProperty("email_verified", out JsonElement verifiedEl) &&
                    verifiedEl.ValueKind == JsonValueKind.False)
                {
                    throw new ValidationException("idToken", "Google account email is not verified.");
                }

                string sub = root.TryGetProperty("sub", out JsonElement subEl) && !string.IsNullOrWhiteSpace(subEl.GetString())
                    ? subEl.GetString()!
                    : throw new ValidationException("idToken", "Google OIDC ID token is missing subject (sub) claim.");

                string claimEmail = root.TryGetProperty("email", out JsonElement emailEl) && !string.IsNullOrWhiteSpace(emailEl.GetString())
                    ? emailEl.GetString()!.Trim().ToLowerInvariant()
                    : throw new ValidationException("idToken", "Google OIDC ID token is missing email claim.");

                string claimName = root.TryGetProperty("name", out JsonElement nameEl) && !string.IsNullOrWhiteSpace(nameEl.GetString())
                    ? nameEl.GetString()!.Trim()
                    : "Google Verified Engineer";

                return (sub, claimEmail, claimName);
            }
            catch (FormatException ex)
            {
                throw new ValidationException("idToken", $"Malformed Google OIDC JWT payload: {ex.Message}");
            }
            catch (JsonException ex)
            {
                throw new ValidationException("idToken", $"Invalid Google OIDC JSON claims: {ex.Message}");
            }
        }

        // Deterministic mock adapter token support (must start with "google_oidc_" or "ya29.")
        if (!rawToken.StartsWith("google_oidc_", StringComparison.Ordinal) &&
            !rawToken.StartsWith("ya29.", StringComparison.Ordinal))
        {
            throw new ValidationException("idToken", "Unrecognized Google OIDC ID token format.");
        }

        string subjectId = $"google-sub-{_tokenService.HashToken(rawToken)[..24]}";
        string email = string.IsNullOrWhiteSpace(request.ExpectedEmail)
            ? $"oauth.{subjectId[..12]}@mechware.com.bd"
            : request.ExpectedEmail.Trim().ToLowerInvariant();
        string fullName = string.IsNullOrWhiteSpace(request.FullName)
            ? "Google Verified Engineer"
            : request.FullName.Trim();

        return (subjectId, email, fullName);
    }

    private static byte[] FromBase64Url(string input)
    {
        string padded = input.Replace('-', '+').Replace('_', '/');
        switch (padded.Length % 4)
        {
            case 2: padded += "=="; break;
            case 3: padded += "="; break;
        }
        return Convert.FromBase64String(padded);
    }

    /// <summary>
    /// Validates the full 3-level Bangladesh administrative hierarchy (Division -> District -> Upazila)
    /// against pre-seeded BD locations (AUD-P3-07).
    /// </summary>
    private void ValidateBdLocationHierarchy(string division, string district, string upazila)
    {
        BdLocation? matchedDivision = _bdLocations.FirstOrDefault(l =>
            l.Level == "division" &&
            (string.Equals(l.NameEn, division?.Trim(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(l.NameBn, division?.Trim(), StringComparison.Ordinal)));

        if (matchedDivision is null)
        {
            throw new ValidationException("division", $"'{division}' is not a recognized Bangladesh division.");
        }

        if (string.IsNullOrWhiteSpace(district))
        {
            throw new ValidationException("district", "District is required.");
        }

        BdLocation? matchedDistrict = _bdLocations.FirstOrDefault(l =>
            l.Level == "district" &&
            l.ParentId == matchedDivision.Id &&
            (string.Equals(l.NameEn, district.Trim(), StringComparison.OrdinalIgnoreCase) ||
             string.Equals(l.NameBn, district.Trim(), StringComparison.Ordinal)));

        if (matchedDistrict is null)
        {
            throw new ValidationException("district", $"District '{district}' does not belong to division '{matchedDivision.NameEn}'.");
        }

        if (string.IsNullOrWhiteSpace(upazila))
        {
            throw new ValidationException("upazila", "Upazila / Industrial Zone is required.");
        }

        IReadOnlyList<BdLocation> seededUpazilasForDistrict = _bdLocations
            .Where(l => l.Level == "upazila" && l.ParentId == matchedDistrict.Id)
            .ToList();

        if (seededUpazilasForDistrict.Count > 0)
        {
            bool validUpazila = seededUpazilasForDistrict.Any(l =>
                string.Equals(l.NameEn, upazila.Trim(), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(l.NameBn, upazila.Trim(), StringComparison.Ordinal));

            if (!validUpazila)
            {
                throw new ValidationException("upazila", $"Upazila '{upazila}' does not belong to district '{matchedDistrict.NameEn}'.");
            }
        }
    }

    private AuthTokenResponseDto IssueSessionTokens(
        User user,
        Guid familyId,
        string? clientIp,
        string? userAgent,
        DateTime utcNow)
    {
        IReadOnlyList<string> roles = GetUserRoles(user);
        IReadOnlyList<string> permissions = GetUserPermissions(roles);

        string accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, utcNow);
        string rawRefreshToken = _tokenService.GenerateRawRefreshToken();
        string refreshTokenHash = _tokenService.HashToken(rawRefreshToken);

        _db.RefreshTokens.Add(new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            refreshTokenHash,
            familyId,
            clientIp,
            userAgent,
            utcNow.AddDays(7)));

        return new AuthTokenResponseDto(
            accessToken,
            900,
            "Bearer",
            MapUserDto(user),
            RefreshTokenCookieValue: rawRefreshToken);
    }

    private (OtpCode Otp, string RawCode) CreateOtp(string recipient, OtpType type, DateTime utcNow)
    {
        int numericCode = RandomNumberGenerator.GetInt32(100_000, 1_000_000);
        string rawCode = numericCode.ToString("D6");
        string codeHash = _tokenService.HashToken(rawCode);
        string referenceId = $"ref_{Guid.NewGuid():N}"[..16];

        var otp = new OtpCode(
            Guid.NewGuid(),
            recipient,
            codeHash,
            type,
            referenceId,
            utcNow.AddMinutes(5));

        _db.OtpCodes.Add(otp);
        return (otp, rawCode);
    }

    /// <summary>Loads the newest matching OTP and holds a row lock until the surrounding transaction ends.</summary>
    private async Task<OtpCode?> LockOtpAsync(string recipient, string referenceId, OtpType? type, CancellationToken cancellationToken)
    {
        IQueryable<OtpCode> query = type is null
            ? _db.OtpCodes.FromSqlInterpolated($@"SELECT * FROM otp_codes
                WHERE lower(recipient) = {recipient} AND reference_id = {referenceId}
                ORDER BY created_at DESC LIMIT 1 FOR UPDATE")
            : _db.OtpCodes.FromSqlInterpolated($@"SELECT * FROM otp_codes
                WHERE lower(recipient) = {recipient} AND reference_id = {referenceId} AND type = {type.Value.ToString()}
                ORDER BY created_at DESC LIMIT 1 FOR UPDATE");

        return (await query.ToListAsync(cancellationToken)).FirstOrDefault();
    }

    private bool VerifyUserTwoFactorOrRecoveryCode(User user, string code, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(user.TwoFactorSecret))
        {
            return false;
        }

        var timestamp = new DateTimeOffset(DateTime.SpecifyKind(utcNow, DateTimeKind.Utc));
        if (_totpService.TryVerifyTotpCode(
            user.TwoFactorSecret,
            code,
            timestamp,
            user.LastUsedTotpTimeStep,
            out long matchedStep))
        {
            user.RecordTotpTimeStepUsed(matchedStep, utcNow);
            return true;
        }

        if (!string.IsNullOrWhiteSpace(user.TwoFactorRecoveryCodesJson))
        {
            List<string>? hashes = JsonSerializer.Deserialize<List<string>>(user.TwoFactorRecoveryCodesJson);
            if (hashes is not null)
            {
                string candidateHash = _totpService.HashRecoveryCode(code);
                if (hashes.Remove(candidateHash))
                {
                    user.UpdateRecoveryCodesJson(JsonSerializer.Serialize(hashes), utcNow);
                    return true;
                }
            }
        }

        return false;
    }

    private async Task RevokeTokenFamilyAsync(Guid familyId, DateTime utcNow, CancellationToken cancellationToken)
    {
        List<RefreshToken> tokens = await _db.RefreshTokens.Where(rt => rt.FamilyId == familyId && !rt.IsRevoked).ToListAsync(cancellationToken);
        foreach (RefreshToken token in tokens)
        {
            token.Revoke(utcNow);
        }
    }

    private async Task RevokeAllUserSessionsAsync(Guid userId, DateTime utcNow, CancellationToken cancellationToken)
    {
        List<RefreshToken> tokens = await _db.RefreshTokens.Where(rt => rt.UserId == userId && !rt.IsRevoked).ToListAsync(cancellationToken);
        foreach (RefreshToken token in tokens)
        {
            token.Revoke(utcNow);
        }
    }

    private async Task ClearConflictingDefaultAddressesAsync(
        Guid userId,
        bool clearShipping,
        bool clearBilling,
        DateTime utcNow,
        Guid? excludeAddressId,
        CancellationToken cancellationToken)
    {
        List<Address> others = await _db.Addresses.Where(a => a.UserId == userId && a.Id != excludeAddressId).ToListAsync(cancellationToken);
        foreach (Address existing in others)
        {
            bool nextShipping = clearShipping ? false : existing.IsDefaultShipping;
            bool nextBilling = clearBilling ? false : existing.IsDefaultBilling;
            existing.SetDefaultFlags(nextShipping, nextBilling, utcNow);
        }
    }

    private async Task<User?> FindUserByIdentifierAsync(string identifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        string trimmed = identifier.Trim();
        string lowered = trimmed.ToLowerInvariant();
        return await _db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(
                u => u.DeletedAtUtc == null && (u.Email.ToLower() == lowered || (u.Phone != null && u.Phone == trimmed)),
                cancellationToken);
    }

    private async Task<User> GetUserByIdOrThrowAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAtUtc == null, cancellationToken)
            ?? throw new KeyNotFoundException("User account was not found.");

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    private static IReadOnlyList<string> GetUserRoles(User user)
    {
        List<string> roles = user.UserRoles.Select(ur => ur.RoleName).Distinct().ToList();
        return roles.Count > 0 ? roles.AsReadOnly() : [Permissions.Roles.Buyer];
    }

    private static IReadOnlyList<string> GetUserPermissions(IReadOnlyList<string> roles) =>
        roles.SelectMany(Permissions.GetDefaultPermissionsForRole)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList()
            .AsReadOnly();

    private static AuthenticatedUserDto MapUserDto(User user)
    {
        IReadOnlyList<string> roles = GetUserRoles(user);
        IReadOnlyList<string> permissions = GetUserPermissions(roles);

        return new AuthenticatedUserDto(
            user.Id,
            user.Email,
            user.Phone,
            user.FullName,
            roles,
            permissions,
            user.Locale,
            user.TwoFactorEnabled,
            user.SellerId);
    }

    private static AddressDto MapAddressDto(Address a) =>
        new(
            a.Id,
            a.Label,
            a.RecipientName,
            a.Phone,
            a.Line1,
            a.Line2,
            a.Area,
            a.Division,
            a.District,
            a.Upazila,
            a.PostalCode,
            a.Country,
            a.IsDefaultShipping,
            a.IsDefaultBilling,
            a.Latitude,
            a.Longitude);

    /// <summary>Queues a semantic audit event; it is saved together with the change it describes.</summary>
    private void RecordAudit(
        Guid? actorId,
        string? actorIp,
        string action,
        string entityName,
        string entityId,
        object? oldValues,
        object? newValues)
    {
        _db.AuditLogs.Add(new AuditLog(
            Guid.NewGuid(),
            actorId,
            actorIp,
            action,
            entityName,
            entityId,
            oldValues is null ? null : JsonSerializer.Serialize(oldValues),
            newValues is null ? null : JsonSerializer.Serialize(newValues),
            null,
            DateTime.UtcNow));
    }
}
