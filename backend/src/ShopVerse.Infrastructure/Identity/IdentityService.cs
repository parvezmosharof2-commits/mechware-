using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ShopVerse.Application.Identity;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Infrastructure.Identity;

/// <summary>
/// Core Identity, Authentication, Token Rotation/Family Breach Detection, TOTP 2FA with Replay Guard,
/// Address Book, Bangladesh Location Hierarchy, and GDPR Export/Erasure service.
/// </summary>
public sealed class IdentityService : IIdentityService
{
    public static readonly Guid SellerAlphaId = Guid.Parse("aaaa0000-0000-0000-0000-000000000001");
    public static readonly Guid SellerBetaId = Guid.Parse("bbbb0000-0000-0000-0000-000000000002");

    private readonly IPasswordHasherService _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ITotpService _totpService;
    private readonly ISmsProvider _smsProvider;

    private readonly ConcurrentDictionary<Guid, User> _users = new();
    private readonly ConcurrentDictionary<Guid, RefreshToken> _refreshTokens = new();
    private readonly ConcurrentDictionary<Guid, OtpCode> _otpCodes = new();
    private readonly ConcurrentDictionary<Guid, Address> _addresses = new();
    private readonly ConcurrentQueue<AuditLog> _auditLogs = new();
    private readonly IReadOnlyList<BdLocation> _bdLocations;
    private readonly object _tokenLock = new();

    public IdentityService(
        IPasswordHasherService passwordHasher,
        ITokenService tokenService,
        ITotpService totpService,
        ISmsProvider smsProvider,
        IdentitySeedPolicy seedPolicy,
        BootstrapAdminSettings? bootstrapAdmin = null)
    {
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _totpService = totpService;
        _smsProvider = smsProvider;
        _bdLocations = BangladeshLocationSeeder.GetSeedLocations();

        if (seedPolicy.SeedDemoAccounts)
        {
            SeedEnterpriseAccounts();
        }

        if (bootstrapAdmin is not null)
        {
            SeedBootstrapAdmin(bootstrapAdmin);
        }
    }

    private void SeedBootstrapAdmin(BootstrapAdminSettings settings)
    {
        _passwordHasher.ValidatePasswordPolicy(settings.Password);

        if (_users.Values.Any(u => string.Equals(u.Email, settings.Email, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        var now = DateTime.UtcNow;
        var admin = User.Create(settings.Email, settings.Phone, _passwordHasher.HashPassword(settings.Password), settings.FullName, "en");
        admin.VerifyEmail(now);
        if (settings.Phone is not null)
        {
            admin.VerifyPhone(now);
        }

        admin.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000001"), Permissions.Roles.Admin);
        admin.ConfigureTwoFactorSecret(settings.TotpSecret, JsonSerializer.Serialize(Array.Empty<string>()), now);
        admin.EnableTwoFactor(now);
        _users[admin.Id] = admin;
        RecordAudit(admin.Id, null, "user.bootstrap_admin_provisioned", "users", admin.Id.ToString(), null, new { admin.Email });
    }

    private void SeedEnterpriseAccounts()
    {
        var now = DateTime.UtcNow;

        // 1. Admin account (Mandatory 2FA enabled with known deterministic base32 secret for tests/demo)
        var admin = User.Create(
            "admin@mechware.com.bd",
            "+8801711000001",
            _passwordHasher.HashPassword("MechAdmin#2026!"),
            "Parvez Mosharof (Principal Admin)",
            "en");
        admin.VerifyEmail(now);
        admin.VerifyPhone(now);
        admin.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000001"), Permissions.Roles.Admin);
        string adminTotpSecret = "JBSWY3DPEHPK3PXPJBSWY3DPEHPK3PXP";
        var recoveryHashes = new[] { _totpService.HashRecoveryCode("ABCDE-12345") };
        admin.ConfigureTwoFactorSecret(adminTotpSecret, JsonSerializer.Serialize(recoveryHashes), now);
        admin.EnableTwoFactor(now);
        _users[admin.Id] = admin;

        // 2. Seller Alpha (Owns SellerAlphaId)
        var sellerAlpha = User.Create(
            "seller.alpha@mechware.com.bd",
            "+8801711000002",
            _passwordHasher.HashPassword("SellerAlpha#2026!"),
            "Dhaka Heavy Pneumatics Ltd.",
            "en",
            SellerAlphaId);
        sellerAlpha.VerifyEmail(now);
        sellerAlpha.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000002"), Permissions.Roles.Seller);
        _users[sellerAlpha.Id] = sellerAlpha;

        // 3. Seller Beta (Owns SellerBetaId)
        var sellerBeta = User.Create(
            "seller.beta@mechware.com.bd",
            "+8801711000003",
            _passwordHasher.HashPassword("SellerBeta#2026!"),
            "Chattogram Marine Automation",
            "bn",
            SellerBetaId);
        sellerBeta.VerifyEmail(now);
        sellerBeta.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000002"), Permissions.Roles.Seller);
        _users[sellerBeta.Id] = sellerBeta;

        // 4. Verified Industrial Buyer
        var buyer = User.Create(
            "buyer@mechware.com.bd",
            "+8801711000004",
            _passwordHasher.HashPassword("BuyerPass#2026!"),
            "Gazipur Textile Mills Procurement",
            "en");
        buyer.VerifyEmail(now);
        buyer.VerifyPhone(now);
        buyer.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
        _users[buyer.Id] = buyer;

        var defaultAddress = new Address(
            Guid.NewGuid(),
            buyer.Id,
            "Factory Plant #1",
            "Engr. Tanvir Ahmed",
            "+8801711000004",
            "Plot 42, Sector 7, Tongi Industrial Zone",
            "Gate 3, Receiving Bay B",
            "Tongi",
            "Tongi Industrial Zone",
            "Gazipur",
            "Dhaka",
            "1710",
            true,
            true,
            23.8915m,
            90.4023m);
        _addresses[defaultAddress.Id] = defaultAddress;
    }

    public async Task<(RegisterResponseDto Response, string RawOtpForDev)> RegisterAsync(
        RegisterRequestDto request,
        string? clientIp = null,
        CancellationToken cancellationToken = default)
    {
        var now = DateTime.UtcNow;
        _passwordHasher.ValidatePasswordPolicy(request.Password);

        string normalizedEmail = request.Email.Trim().ToLowerInvariant();
        if (_users.Values.Any(u => u.DeletedAtUtc == null && string.Equals(u.Email, normalizedEmail, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ValidationException("email", "An account with this email address already exists.");
        }

        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            string normalizedPhone = request.Phone.Trim();
            if (_users.Values.Any(u => u.DeletedAtUtc == null && string.Equals(u.Phone, normalizedPhone, StringComparison.OrdinalIgnoreCase)))
            {
                throw new ValidationException("phone", "An account with this phone number already exists.");
            }
        }

        string passwordHash = _passwordHasher.HashPassword(request.Password);
        var user = User.Create(normalizedEmail, request.Phone, passwordHash, request.FullName, request.Locale);
        user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
        _users[user.Id] = user;

        bool verifyByPhone = !string.IsNullOrWhiteSpace(user.Phone);
        string recipient = verifyByPhone ? user.Phone! : user.Email;
        OtpType otpType = verifyByPhone ? OtpType.PhoneVerify : OtpType.EmailVerify;

        (OtpCode otp, string rawCode) = CreateOtp(recipient, otpType, now);

        if (verifyByPhone)
        {
            await _smsProvider.SendSmsAsync(
                recipient,
                $"MECHWARE Verification Code: {rawCode}. Valid for 5 minutes. Ref: {otp.ReferenceId}",
                cancellationToken);
        }

        RecordAudit(user.Id, clientIp, "user.registered", "users", user.Id.ToString(), null, new { user.Email, user.Phone, user.Locale });

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

        if (recipient.StartsWith('+') || recipient.All(char.IsDigit))
        {
            await _smsProvider.SendSmsAsync(
                recipient,
                $"MECHWARE Security Code: {rawCode}. Valid for 5 minutes. Ref: {otp.ReferenceId}",
                cancellationToken);
        }

        User? user = FindUserByIdentifier(recipient);
        RecordAudit(user?.Id, clientIp, "user.otp_dispatched", "otp_codes", otp.Id.ToString(), null, new { recipient, otp.Type, otp.ReferenceId });

        return (new SendOtpResponseDto(recipient, otpType.ToString(), otp.ReferenceId, 300, OtpCode.MaxVerificationAttempts), rawCode);
    }

    public AuthTokenResponseDto VerifyOtp(
        VerifyOtpRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        string recipient = request.Recipient.Trim().ToLowerInvariant();

        OtpCode? otp = _otpCodes.Values
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefault(o =>
                string.Equals(o.Recipient, recipient, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.ReferenceId, request.ReferenceId, StringComparison.Ordinal));

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
            throw new ValidationException("code", "Invalid verification code.");
        }

        otp.MarkConsumed(now);

        User? user = FindUserByIdentifier(recipient);
        if (user is null)
        {
            throw new KeyNotFoundException("User account for OTP recipient was not found.");
        }

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

        return IssueSessionTokens(user, Guid.NewGuid(), clientIp, userAgent, now);
    }

    public AuthTokenResponseDto Login(
        LoginRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        User? user = FindUserByIdentifier(request.Identifier);

        if (user is null || user.DeletedAtUtc.HasValue || string.IsNullOrEmpty(user.PasswordHash))
        {
            throw new UnauthorizedAccessException("Invalid credentials.");
        }

        if (user.IsLockedOut(now))
        {
            RecordAudit(user.Id, clientIp, "user.login_locked_out", "users", user.Id.ToString(), null, new { user.LockoutUntilUtc });
            throw new UnauthorizedAccessException($"Account is temporarily locked due to multiple failed login attempts until {user.LockoutUntilUtc:O}.");
        }

        if (!_passwordHasher.VerifyPassword(request.Password, user.PasswordHash))
        {
            user.RecordFailedLogin(now);
            RecordAudit(user.Id, clientIp, "user.login_failed", "users", user.Id.ToString(), null, new { user.FailedLoginAttempts, user.LockoutUntilUtc });
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
                throw new UnauthorizedAccessException("Invalid or replayed two-factor authentication / recovery code.");
            }
        }

        user.RecordSuccessfulLogin(now);
        RecordAudit(user.Id, clientIp, "user.login_succeeded", "users", user.Id.ToString(), null, new { clientIp });

        return IssueSessionTokens(user, Guid.NewGuid(), clientIp, userAgent, now);
    }

    public AuthTokenResponseDto GoogleLogin(
        GoogleLoginRequestDto request,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(request.IdToken) || request.IdToken.Length < 10)
        {
            throw new ValidationException("idToken", "Valid Google OIDC ID token is required.");
        }

        (string subjectId, string email, string fullName) = ValidateAndExtractGoogleOidcClaims(request, now);

        User? user = _users.Values.FirstOrDefault(u =>
            u.DeletedAtUtc == null &&
            (string.Equals(u.GoogleSubjectId, subjectId, StringComparison.Ordinal) ||
             string.Equals(u.Email, email, StringComparison.OrdinalIgnoreCase)));

        if (user is null)
        {
            user = User.Create(email, null, null, fullName, "en");
            user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
            _users[user.Id] = user;
        }

        user.LinkGoogleAccount(subjectId, now);
        user.RecordSuccessfulLogin(now);

        RecordAudit(user.Id, clientIp, "user.google_oauth_login", "users", user.Id.ToString(), null, new { email, subjectId });

        return IssueSessionTokens(user, Guid.NewGuid(), clientIp, userAgent, now);
    }

    /// <summary>
    /// Rotates a refresh token. Implements ADR-0006:
    /// - 10-second multi-tab/SSR race grace window from same IP returns valid access token without revoking family.
    /// - Any reuse outside the grace window or from a different IP triggers immediate revocation of the entire token family!
    /// </summary>
    public AuthTokenResponseDto RotateRefreshToken(
        string rawRefreshToken,
        string? clientIp = null,
        string? userAgent = null,
        DateTime? utcNowOverride = null)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            throw new UnauthorizedAccessException("Refresh token is missing.");
        }

        var now = utcNowOverride ?? DateTime.UtcNow;
        string tokenHash = _tokenService.HashToken(rawRefreshToken);

        lock (_tokenLock)
        {
            RefreshToken? storedToken = _refreshTokens.Values.FirstOrDefault(rt =>
                string.Equals(rt.TokenHash, tokenHash, StringComparison.Ordinal));

            if (storedToken is null)
            {
                throw new UnauthorizedAccessException("Invalid refresh token.");
            }

            User? user = _users.GetValueOrDefault(storedToken.UserId);
            if (user is null || user.DeletedAtUtc.HasValue || user.Status == UserStatus.Banned)
            {
                throw new UnauthorizedAccessException("User account is no longer active.");
            }

            if (storedToken.IsUsed)
            {
                // Check ADR-0006 10-second multi-tab/SSR grace window
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

                // BREACH DETECTED: Token reuse outside grace window or from different IP!
                // Revoke the ENTIRE token family immediately.
                RevokeTokenFamilyInternal(storedToken.FamilyId, now);
                RecordAudit(
                    user.Id,
                    clientIp,
                    "security.refresh_token_family_revoked_on_reuse",
                    "refresh_tokens",
                    storedToken.FamilyId.ToString(),
                    new { storedToken.CreatedByIp, storedToken.UsedAtUtc },
                    new { BreachIp = clientIp, DetectedAtUtc = now });

                throw new UnauthorizedAccessException("Refresh token reuse detected. Entire session family has been revoked for security.");
            }

            if (storedToken.IsRevoked || storedToken.IsExpired(now))
            {
                throw new UnauthorizedAccessException("Refresh token is expired or revoked.");
            }

            string newRawRefresh = _tokenService.GenerateRawRefreshToken();
            string newRefreshHash = _tokenService.HashToken(newRawRefresh);

            storedToken.MarkUsed(newRefreshHash, now);

            var nextToken = new RefreshToken(
                Guid.NewGuid(),
                user.Id,
                newRefreshHash,
                storedToken.FamilyId,
                clientIp,
                userAgent,
                now.AddDays(7));

            _refreshTokens[nextToken.Id] = nextToken;

            IReadOnlyList<string> userRoles = GetUserRoles(user);
            IReadOnlyList<string> userPermissions = GetUserPermissions(userRoles);
            string newAccessToken = _tokenService.GenerateAccessToken(user, userRoles, userPermissions, now);

            return new AuthTokenResponseDto(
                newAccessToken,
                900,
                "Bearer",
                MapUserDto(user),
                RefreshTokenCookieValue: newRawRefresh);
        }
    }

    /// <summary>
    /// Revokes the specific refresh token family associated with the caller's httpOnly refresh_token cookie
    /// (AUD-P3-05: supports logout even when the 15-minute Access JWT has expired, without wiping other device sessions).
    /// </summary>
    public bool RevokeRefreshTokenByCookie(
        string? rawRefreshToken,
        string? clientIp = null,
        DateTime? utcNowOverride = null)
    {
        if (string.IsNullOrWhiteSpace(rawRefreshToken))
        {
            return false;
        }

        var now = utcNowOverride ?? DateTime.UtcNow;
        string tokenHash = _tokenService.HashToken(rawRefreshToken);

        lock (_tokenLock)
        {
            RefreshToken? storedToken = _refreshTokens.Values.FirstOrDefault(rt =>
                string.Equals(rt.TokenHash, tokenHash, StringComparison.Ordinal));

            if (storedToken is null)
            {
                return false;
            }

            RevokeTokenFamilyInternal(storedToken.FamilyId, now);
            RecordAudit(
                storedToken.UserId,
                clientIp,
                "user.session_logged_out",
                "refresh_tokens",
                storedToken.FamilyId.ToString(),
                null,
                new { storedToken.FamilyId });

            return true;
        }
    }

    public (string ReferenceId, string RawOtpForDev) InitiatePasswordReset(string identifier, string? clientIp = null)
    {
        var now = DateTime.UtcNow;
        User? user = FindUserByIdentifier(identifier);
        string recipient = user?.Email ?? identifier.Trim().ToLowerInvariant();

        (OtpCode otp, string rawCode) = CreateOtp(recipient, OtpType.PasswordReset, now);

        if (user is not null)
        {
            RecordAudit(user.Id, clientIp, "user.password_reset_requested", "users", user.Id.ToString(), null, new { otp.ReferenceId });
        }

        return (otp.ReferenceId, rawCode);
    }

    public void CompletePasswordReset(ResetPasswordRequestDto request, string? clientIp = null)
    {
        var now = DateTime.UtcNow;
        _passwordHasher.ValidatePasswordPolicy(request.NewPassword);

        string recipient = request.Recipient.Trim().ToLowerInvariant();
        OtpCode? otp = _otpCodes.Values
            .OrderByDescending(o => o.CreatedAtUtc)
            .FirstOrDefault(o =>
                o.Type == OtpType.PasswordReset &&
                string.Equals(o.Recipient, recipient, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(o.ReferenceId, request.ReferenceId, StringComparison.Ordinal));

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
            throw new ValidationException("otpCode", "Invalid password reset code.");
        }

        otp.MarkConsumed(now);

        User? user = FindUserByIdentifier(recipient)
            ?? throw new KeyNotFoundException("User account was not found.");

        string newHash = _passwordHasher.HashPassword(request.NewPassword);
        user.UpdatePasswordHash(newHash, now);

        // Revoke all existing refresh tokens on password change
        RevokeAllUserSessions(user.Id, now);
        RecordAudit(user.Id, clientIp, "user.password_reset_completed", "users", user.Id.ToString(), null, null);
    }

    public TotpSetupResponseDto SetupTwoFactor(Guid userId, string? clientIp = null)
    {
        var now = DateTime.UtcNow;
        User user = GetUserByIdOrThrow(userId);

        string secret = _totpService.GenerateBase32Secret();
        IReadOnlyList<string> rawRecoveryCodes = _totpService.GenerateRecoveryCodes(10);
        string hashedCodesJson = JsonSerializer.Serialize(rawRecoveryCodes.Select(_totpService.HashRecoveryCode).ToList());

        user.ConfigureTwoFactorSecret(secret, hashedCodesJson, now);
        string otpAuthUri = _totpService.BuildOtpAuthUri(user.Email, secret);

        RecordAudit(user.Id, clientIp, "user.2fa_setup_initiated", "users", user.Id.ToString(), null, null);

        return new TotpSetupResponseDto(secret, otpAuthUri, rawRecoveryCodes);
    }

    public void VerifyAndEnableTwoFactor(Guid userId, string code, string? clientIp = null, DateTimeOffset? timestampOverride = null)
    {
        var now = timestampOverride ?? DateTimeOffset.UtcNow;
        User user = GetUserByIdOrThrow(userId);

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
    }

    public IReadOnlyList<ActiveSessionDto> GetActiveSessions(Guid userId, string? currentRawRefreshToken = null)
    {
        var now = DateTime.UtcNow;
        string? currentHash = string.IsNullOrWhiteSpace(currentRawRefreshToken)
            ? null
            : _tokenService.HashToken(currentRawRefreshToken);

        return _refreshTokens.Values
            .Where(rt => rt.UserId == userId && rt.IsActive(now))
            .OrderByDescending(rt => rt.CreatedAtUtc)
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

    public int RevokeSessions(
        Guid userId,
        RevokeSessionsRequestDto request,
        string? clientIp = null,
        string? currentRawRefreshToken = null)
    {
        var now = DateTime.UtcNow;
        int revokedCount = 0;
        string? currentHash = string.IsNullOrWhiteSpace(currentRawRefreshToken)
            ? null
            : _tokenService.HashToken(currentRawRefreshToken);

        lock (_tokenLock)
        {
            Guid? currentFamilyId = currentHash is null
                ? null
                : _refreshTokens.Values.FirstOrDefault(rt =>
                    rt.UserId == userId &&
                    string.Equals(rt.TokenHash, currentHash, StringComparison.Ordinal))?.FamilyId;

            IEnumerable<RefreshToken> targetTokens = _refreshTokens.Values.Where(rt => rt.UserId == userId && !rt.IsRevoked);

            if (request.SessionId.HasValue)
            {
                targetTokens = targetTokens.Where(rt => rt.Id == request.SessionId.Value || rt.FamilyId == request.SessionId.Value);
            }
            else if (request.RevokeAllExceptCurrent && currentFamilyId.HasValue)
            {
                targetTokens = targetTokens.Where(rt => rt.FamilyId != currentFamilyId.Value);
            }
            else if (!request.RevokeAll && currentFamilyId.HasValue)
            {
                targetTokens = targetTokens.Where(rt => rt.FamilyId == currentFamilyId.Value);
            }

            foreach (RefreshToken token in targetTokens)
            {
                token.Revoke(now);
                revokedCount++;
            }
        }

        RecordAudit(userId, clientIp, "user.sessions_revoked", "refresh_tokens", userId.ToString(), null, new { revokedCount, request.RevokeAll, request.RevokeAllExceptCurrent });
        return revokedCount;
    }

    public IReadOnlyList<AddressDto> GetAddresses(Guid userId) =>
        _addresses.Values
            .Where(a => a.UserId == userId)
            .OrderByDescending(a => a.IsDefaultShipping)
            .ThenByDescending(a => a.CreatedAtUtc)
            .Select(MapAddressDto)
            .ToList()
            .AsReadOnly();

    public AddressDto CreateAddress(Guid userId, UpsertAddressRequestDto request, string? clientIp = null)
    {
        var now = DateTime.UtcNow;
        ValidateBdLocationHierarchy(request.Division, request.District, request.Upazila);

        if (request.IsDefaultShipping || request.IsDefaultBilling)
        {
            ClearConflictingDefaultAddresses(userId, request.IsDefaultShipping, request.IsDefaultBilling, now);
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

        _addresses[address.Id] = address;
        RecordAudit(userId, clientIp, "address.created", "addresses", address.Id.ToString(), null, new { address.Division, address.District, address.Upazila });

        return MapAddressDto(address);
    }

    public AddressDto UpdateAddress(Guid userId, Guid addressId, UpsertAddressRequestDto request, string? clientIp = null)
    {
        var now = DateTime.UtcNow;
        if (!_addresses.TryGetValue(addressId, out Address? address) || address.UserId != userId)
        {
            throw new KeyNotFoundException("Address not found.");
        }

        ValidateBdLocationHierarchy(request.Division, request.District, request.Upazila);

        if (request.IsDefaultShipping || request.IsDefaultBilling)
        {
            ClearConflictingDefaultAddresses(userId, request.IsDefaultShipping, request.IsDefaultBilling, now, excludeAddressId: addressId);
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

        return MapAddressDto(address);
    }

    public void DeleteAddress(Guid userId, Guid addressId, string? clientIp = null)
    {
        if (!_addresses.TryGetValue(addressId, out Address? address) || address.UserId != userId)
        {
            throw new KeyNotFoundException("Address not found.");
        }

        _addresses.TryRemove(addressId, out _);
        RecordAudit(userId, clientIp, "address.deleted", "addresses", addressId.ToString(), new { address.Label, address.Line1 }, null);
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

    public GdprExportBundleDto ExportUserData(Guid userId, string? clientIp = null)
    {
        User user = GetUserByIdOrThrow(userId);
        RecordAudit(userId, clientIp, "user.gdpr_export", "users", userId.ToString(), null, null);

        IReadOnlyList<AuditLogSummaryDto> auditEvents = _auditLogs
            .Where(a => a.ActorId == userId)
            .OrderByDescending(a => a.CreatedAtUtc)
            .Take(100)
            .Select(a => new AuditLogSummaryDto(a.Id, a.Action, a.EntityName, a.EntityId, a.ActorIp, a.CreatedAtUtc))
            .ToList()
            .AsReadOnly();

        return new GdprExportBundleDto(
            DateTime.UtcNow,
            MapUserDto(user),
            GetAddresses(userId),
            GetActiveSessions(userId),
            auditEvents);
    }

    public void AnonymizeAndEraseUser(Guid userId, string? clientIp = null)
    {
        var now = DateTime.UtcNow;
        User user = GetUserByIdOrThrow(userId);

        user.AnonymizeForGdpr(now);
        RevokeAllUserSessions(userId, now);

        foreach (Guid addrId in _addresses.Values.Where(a => a.UserId == userId).Select(a => a.Id).ToList())
        {
            _addresses.TryRemove(addrId, out _);
        }

        RecordAudit(
            userId,
            clientIp,
            "user.gdpr_anonymized",
            "users",
            userId.ToString(),
            new { EmailRedacted = true, PreviousStatus = "Active" },
            new { user.Email, Status = user.Status.ToString() });
    }

    public AuthenticatedUserDto GetUserProfile(Guid userId)
    {
        User user = GetUserByIdOrThrow(userId);
        return MapUserDto(user);
    }

    public IReadOnlyList<User> GetUsersForAudit() => [.. _users.Values];

    public IReadOnlyList<AuditLog> GetAuditLogs() => [.. _auditLogs];

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

        var refreshToken = new RefreshToken(
            Guid.NewGuid(),
            user.Id,
            refreshTokenHash,
            familyId,
            clientIp,
            userAgent,
            utcNow.AddDays(7));

        _refreshTokens[refreshToken.Id] = refreshToken;

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

        _otpCodes[otp.Id] = otp;
        return (otp, rawCode);
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

    private void RevokeTokenFamilyInternal(Guid familyId, DateTime utcNow)
    {
        foreach (RefreshToken token in _refreshTokens.Values.Where(rt => rt.FamilyId == familyId && !rt.IsRevoked))
        {
            token.Revoke(utcNow);
        }
    }

    private void RevokeAllUserSessions(Guid userId, DateTime utcNow)
    {
        lock (_tokenLock)
        {
            foreach (RefreshToken token in _refreshTokens.Values.Where(rt => rt.UserId == userId && !rt.IsRevoked))
            {
                token.Revoke(utcNow);
            }
        }
    }

    private void ClearConflictingDefaultAddresses(
        Guid userId,
        bool clearShipping,
        bool clearBilling,
        DateTime utcNow,
        Guid? excludeAddressId = null)
    {
        foreach (Address existing in _addresses.Values.Where(a => a.UserId == userId && a.Id != excludeAddressId))
        {
            bool nextShipping = clearShipping ? false : existing.IsDefaultShipping;
            bool nextBilling = clearBilling ? false : existing.IsDefaultBilling;
            existing.SetDefaultFlags(nextShipping, nextBilling, utcNow);
        }
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

    private User? FindUserByIdentifier(string identifier)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        string trimmed = identifier.Trim();
        return _users.Values.FirstOrDefault(u =>
            u.DeletedAtUtc == null &&
            (string.Equals(u.Email, trimmed, StringComparison.OrdinalIgnoreCase) ||
             (!string.IsNullOrEmpty(u.Phone) && string.Equals(u.Phone, trimmed, StringComparison.OrdinalIgnoreCase))));
    }

    private User GetUserByIdOrThrow(Guid userId)
    {
        if (!_users.TryGetValue(userId, out User? user) || user.DeletedAtUtc.HasValue)
        {
            throw new KeyNotFoundException("User account was not found.");
        }

        return user;
    }

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

    private void RecordAudit(
        Guid? actorId,
        string? actorIp,
        string action,
        string entityName,
        string entityId,
        object? oldValues,
        object? newValues)
    {
        _auditLogs.Enqueue(new AuditLog(
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
