using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using Mechware.Application.Identity;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence;

namespace Mechware.Infrastructure.Identity;

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
    private readonly IOtpSmsGuard _otpSmsGuard;
    private readonly MechwareDbContext _db;
    private readonly IReadOnlyList<BdLocation> _bdLocations;
    private readonly IGoogleIdTokenVerifier? _googleVerifier;

    public IdentityService(
        IPasswordHasherService passwordHasher,
        ITokenService tokenService,
        ITotpService totpService,
        ISmsProvider smsProvider,
        IOtpSmsGuard otpSmsGuard,
        MechwareDbContext db,
        IGoogleIdTokenVerifier? googleVerifier = null)
    {
        _googleVerifier = googleVerifier;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _totpService = totpService;
        _smsProvider = smsProvider;
        _otpSmsGuard = otpSmsGuard;
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
        string? normalizedPhone = null;
        if (!string.IsNullOrWhiteSpace(request.Phone))
        {
            // The verification code goes by SMS, and only Bangladeshi mobiles receive one (F3a.2a). A format rule, so it
            // answers the same whether or not the number has an account.
            normalizedPhone = BdMobileNumber.TryNormalize(request.Phone, out string e164)
                ? e164
                : throw new ValidationException("phone", BdMobileOnlyMessage);
        }

        // Hashing first keeps both outcomes equally slow, so timing does not tell them apart either.
        string passwordHash = _passwordHasher.HashPassword(request.Password);

        // Every validation runs before the existence check (User.Create validates e-mail, phone, name and locale), so an
        // invalid request is refused the same way whether or not the account exists: otherwise "fullName: X" answering
        // 201 for a taken e-mail and 400 for a free one would still tell them apart.
        var user = User.Create(normalizedEmail, normalizedPhone, passwordHash, request.FullName, request.Locale);

        // SMS limits are spent before the existence check, so a number that has an account is throttled exactly like one
        // that does not (the taken path sends nothing, but must not be cheaper to probe).
        if (normalizedPhone is not null)
        {
            await _otpSmsGuard.AcquireAsync(normalizedPhone, clientIp, cancellationToken);
        }

        // Finding #5 of the 2026-11-04 test: registration said "an account with this email address already exists",
        // which tells anyone whether an e-mail or phone number is a customer here. It now answers exactly as for a new
        // account; the owner keeps their account, no second one is created, and the attempt is audited.
        bool taken = await _db.Users.AnyAsync(u => u.DeletedAtUtc == null
            && (u.Email == normalizedEmail || (normalizedPhone != null && u.Phone == normalizedPhone)), cancellationToken);
        if (taken)
        {
            return await IndistinguishableFromNewRegistrationAsync(normalizedEmail, normalizedPhone, clientIp, cancellationToken);
        }

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
            // Two requests raced past the check above; the unique index is the real guard. Same answer as above.
            _db.ChangeTracker.Clear();
            return await IndistinguishableFromNewRegistrationAsync(normalizedEmail, normalizedPhone, clientIp, cancellationToken);
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

    /// <summary>
    /// The answer to a registration whose e-mail or phone already has an account: shaped exactly like a new registration
    /// (random user id, random reference, same channel rule), so nothing in it reveals the existing account. No code is
    /// stored, so the reference can never be verified. The attempt is audited without naming the existing account.
    /// </summary>
    private async Task<(RegisterResponseDto Response, string RawOtpForDev)> IndistinguishableFromNewRegistrationAsync(
        string email, string? phone, string? clientIp, CancellationToken cancellationToken)
    {
        RecordAudit(null, clientIp, "user.registration_for_existing_account", "users", "-", null, new { channel = phone is null ? "email" : "phone" });
        await _db.SaveChangesAsync(cancellationToken);

        var response = new RegisterResponseDto(
            Guid.NewGuid(),
            "pending_verification",
            phone is null ? "email_otp" : "phone_otp",
            $"ref_{Guid.NewGuid():N}"[..16],
            300);
        return (response, string.Empty);
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

        // Anything that is not an e-mail address goes by SMS, and only to a Bangladeshi mobile: refused before any code or
        // message exists. The limits run before the account look-up, so they answer the same for unknown numbers.
        bool bySms = !recipient.Contains('@');
        if (bySms)
        {
            recipient = BdMobileNumber.TryNormalize(recipient, out string e164)
                ? e164
                : throw new ValidationException("recipient", BdMobileOnlyMessage);
            await _otpSmsGuard.AcquireAsync(recipient, clientIp, cancellationToken);
        }

        (OtpCode otp, string rawCode) = CreateOtp(recipient, otpType, now);

        User? user = await FindUserByIdentifierAsync(recipient, cancellationToken);
        RecordAudit(user?.Id, clientIp, "user.otp_dispatched", "otp_codes", otp.Id.ToString(), null, new { recipient, otp.Type, otp.ReferenceId });
        await _db.SaveChangesAsync(cancellationToken);

        // A number without an account gets the same answer but no message: nobody could use that code (verification needs
        // an account), and texting arbitrary numbers is exactly what SMS pumping wants. The real gateway adapter must not
        // make this observable through response time (queue the send rather than await the provider).
        if (bySms && user is not null)
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
        if (BdMobileNumber.TryNormalize(recipient, out string e164))
        {
            recipient = e164;   // the same canonical form the code was stored under (SendOtpAsync, RegisterAsync)
        }

        // The attempt counter is the brute-force limit, so the row is locked while it is checked and updated.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        // Only codes that prove a phone or mailbox count here. A password-reset code is not found at all (so it is neither a
        // sign-in nor spent by trying), and a code type added later stays out until it is listed on purpose.
        OtpCode? otp = await LockOtpAsync(recipient, request.ReferenceId, SignInOtpTypes, cancellationToken);

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

        RecordAudit(user.Id, clientIp, "user.otp_verified", "users", user.Id.ToString(), null, new { otp.Type, otp.ReferenceId });

        // The code proves the phone or mailbox, so that much is kept. It is not the password and the authenticator, so an
        // account that needs both, or one that is locked, gets no session from it.
        Exception? refusal = SessionRefusal(user, now) ?? (RequiresSecondFactor(user) ? PasswordAndSecondFactorRequired() : null);
        if (refusal is not null)
        {
            RecordAudit(user.Id, clientIp, "user.otp_session_refused", "users", user.Id.ToString(), null, new { otp.Type, Reason = refusal.Message });
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            throw refusal;
        }

        user.RecordSuccessfulLogin(now);
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
            // Hash anyway: an unknown account must cost the same time as a wrong password, or the response time
            // tells an attacker which addresses have accounts.
            _passwordHasher.VerifyPassword(request.Password, DummyPasswordHash());
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

        if (SessionRefusal(user, now) is ForbiddenException disabled)
        {
            RecordAudit(user.Id, clientIp, "user.login_refused", "users", user.Id.ToString(), null, new { user.Status });
            await _db.SaveChangesAsync(cancellationToken);
            throw disabled;
        }

        if (RequiresSecondFactor(user) && !user.TwoFactorEnabled)
        {
            // The role demands an authenticator this person does not have yet. The password alone earns nothing; with the
            // one-time setup code an administrator issued, it earns the new secret and a 10-minute token, and the session
            // starts only with the first correct authenticator code. A leaked password alone cannot enrol a stranger's phone.
            if (string.IsNullOrWhiteSpace(request.SetupCode))
            {
                RecordAudit(user.Id, clientIp, "user.2fa_enrollment_required", "users", user.Id.ToString(), null, null);
                await _db.SaveChangesAsync(cancellationToken);
                return new AuthTokenResponseDto(string.Empty, 0, "Bearer", MapUserDto(user), null, RequiresTwoFactorEnrollment: true);
            }

            return await RedeemSetupCodeAsync(user, request.SetupCode, clientIp, now, cancellationToken);
        }

        if (RequiresSecondFactor(user))
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
        if (_googleVerifier is null)
        {
            throw new ForbiddenException("GOOGLE_SIGNIN_DISABLED", "Google sign-in is not configured on this server.");
        }

        (string subjectId, string email, string fullName) = await _googleVerifier.VerifyAsync(request.IdToken, now, cancellationToken);

        // The Google identity already linked wins; the e-mail is only a way to find an account to link for the first time.
        User? user = await _db.Users.Include(u => u.UserRoles)
                .FirstOrDefaultAsync(u => u.DeletedAtUtc == null && u.GoogleSubjectId == subjectId, cancellationToken)
            ?? await _db.Users.Include(u => u.UserRoles)
                .FirstOrDefaultAsync(u => u.DeletedAtUtc == null && u.Email == email, cancellationToken);

        if (user is null)
        {
            user = User.Create(email, null, null, fullName, "en");
            user.AssignRole(Guid.Parse("00000000-0000-0000-0000-000000000003"), Permissions.Roles.Buyer);
            _db.Users.Add(user);
        }
        else
        {
            if (user.GoogleSubjectId is not null && user.GoogleSubjectId != subjectId)
            {
                RecordAudit(user.Id, clientIp, "user.google_oauth_mismatch", "users", user.Id.ToString(), null, new { subjectId });
                await _db.SaveChangesAsync(cancellationToken);
                throw new ConflictException("GOOGLE_ACCOUNT_MISMATCH", "This account is linked to a different Google account.");
            }

            if (SessionRefusal(user, now) is { } refusal)
            {
                RecordAudit(user.Id, clientIp, "user.google_oauth_refused", "users", user.Id.ToString(), null, new { subjectId, Reason = refusal.Message });
                await _db.SaveChangesAsync(cancellationToken);
                throw refusal;
            }

            // Google proves the mailbox, not the password and the authenticator. Such an account is neither signed in nor linked.
            if (RequiresSecondFactor(user))
            {
                RecordAudit(user.Id, clientIp, "user.google_oauth_refused", "users", user.Id.ToString(), null, new { subjectId });
                await _db.SaveChangesAsync(cancellationToken);
                throw PasswordAndSecondFactorRequired();
            }

            // An account whose e-mail was never proven may have been opened by someone else in this address's name, who
            // still holds its password or phone. Linking would hand the real owner an account a stranger can enter.
            if (user.GoogleSubjectId is null && user.EmailVerifiedAtUtc is null)
            {
                RecordAudit(user.Id, clientIp, "user.google_oauth_refused", "users", user.Id.ToString(), null, new { subjectId, Reason = "email_unverified" });
                await _db.SaveChangesAsync(cancellationToken);
                throw new ConflictException("EMAIL_NOT_VERIFIED",
                    "An account with this e-mail exists but has not verified it. Sign in with its password and verify the e-mail first.");
            }
        }

        user.LinkGoogleAccount(subjectId, now);
        user.RecordSuccessfulLogin(now);

        RecordAudit(user.Id, clientIp, "user.google_oauth_login", "users", user.Id.ToString(), null, new { email, subjectId });

        AuthTokenResponseDto response = IssueSessionTokens(user, Guid.NewGuid(), clientIp, userAgent, now);
        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (IsUniqueViolation(ex))
        {
            // Two first sign-ins at once (a double click): the other one created the account. This one may simply retry.
            throw new ConflictException("GOOGLE_SIGNIN_IN_PROGRESS", "This Google account was just being set up. Please try again.");
        }
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
        if (user is null || user.DeletedAtUtc.HasValue || AccountRules.IsDisabled(user.Status))
        {
            throw new UnauthorizedAccessException("User account is no longer active.");
        }

        // A role that now demands an authenticator ends sessions begun without one: the person must sign in and enrol.
        if (AccountRules.MustEnrollSecondFactor(user.TwoFactorEnabled, GetUserRoles(user)))
        {
            // Revoked, not just refused: otherwise these sessions would come back to life once the person enrols, without
            // ever having been through the second factor.
            await RevokeAllUserSessionsAsync(user.Id, now, cancellationToken);
            RecordAudit(user.Id, clientIp, "security.sessions_revoked_2fa_required", "users", user.Id.ToString(), null, null);
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("This account must set up two-factor authentication. Please sign in again.");
        }

        if (storedToken.IsUsed)
        {
            // ADR-0006 10-second multi-tab/SSR grace window
            if (storedToken.IsWithinGraceWindow(now, clientIp, graceSeconds: 10))
            {
                IReadOnlyList<string> roles = GetUserRoles(user);
                IReadOnlyList<string> permissions = GetUserPermissions(roles);
                string accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, now, storedToken.FamilyId);

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
        string newAccessToken = _tokenService.GenerateAccessToken(user, userRoles, userPermissions, now, storedToken.FamilyId);

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
        OtpCode? otp = await LockOtpAsync(recipient, request.ReferenceId, [OtpType.PasswordReset], cancellationToken);

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

        // Re-running setup would swap the secret and recovery codes at once: a stolen access token could lock the owner out
        // of their own authenticator and hand the new one to the thief. Changing an active authenticator needs its own flow.
        if (user.TwoFactorEnabled)
        {
            throw new ConflictException("TWO_FACTOR_ALREADY_ENABLED", "Two-factor authentication is already on for this account.");
        }

        // Staff enrol only with an administrator's setup code (M1); a session from before the role, or a stolen token, must
        // not be a way around it.
        RequireSelfServiceEnrollment(user);

        return await IssueNewTwoFactorSecretAsync(user, clientIp, now, cancellationToken);
    }

    private async Task<TotpSetupResponseDto> IssueNewTwoFactorSecretAsync(User user, string? clientIp, DateTime now, CancellationToken cancellationToken)
    {
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

        RequireSelfServiceEnrollment(user);

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

    public async Task VerifyStepUpAsync(Guid userId, string? code, string action, string? target = null, string? clientIp = null,
        DateTime? utcNowOverride = null, CancellationToken cancellationToken = default)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ForbiddenException("STEP_UP_REQUIRED", "This action needs a fresh code from your authenticator app.");
        }

        User user = await GetUserByIdOrThrowAsync(userId, cancellationToken);
        if (SessionRefusal(user, now) is { } refusal)
        {
            throw refusal;
        }

        // Only a current authenticator code: a recovery code is for a lost phone, not for confirming money moves. The
        // replay guard means one code confirms one action.
        if (user.TwoFactorEnabled && !string.IsNullOrWhiteSpace(user.TwoFactorSecret)
            && _totpService.TryVerifyTotpCode(user.TwoFactorSecret, code.Trim(), new DateTimeOffset(now, TimeSpan.Zero), user.LastUsedTotpTimeStep, out long step))
        {
            user.RecordTotpTimeStepUsed(step, now);
            user.RecordStepUpSuccess(now);
        }
        else
        {
            RecordAudit(user.Id, clientIp, "user.step_up_failed", "users", user.Id.ToString(), null, new { action, target });
            if (user.RecordStepUpFailure(now))
            {
                // Someone holding this session cannot produce the authenticator code: end every session. The owner can
                // still sign in (with password and authenticator); a thief has to start again.
                await RevokeAllUserSessionsAsync(user.Id, now, cancellationToken);
                RecordAudit(user.Id, clientIp, "security.sessions_revoked_step_up_failures", "users", user.Id.ToString(), null, new { action, target });
                await _db.SaveChangesAsync(cancellationToken);
                throw new UnauthorizedAccessException("Too many wrong authenticator codes. Please sign in again.");
            }
            await _db.SaveChangesAsync(cancellationToken);
            throw new ForbiddenException("STEP_UP_FAILED", "That authenticator code is not right, or was already used. Use the next code.");
        }

        RecordAudit(user.Id, clientIp, "user.step_up_passed", "users", user.Id.ToString(), null, new { action, target });
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<StaffAwaitingTwoFactorDto>> ListStaffAwaitingTwoFactorAsync(CancellationToken cancellationToken = default)
    {
        string[] staffRoles = [Permissions.Roles.Admin, Permissions.Roles.Finance, Permissions.Roles.Support, Permissions.Roles.Moderator];
        var users = await _db.Users.AsNoTracking().Include(u => u.UserRoles)
            .Where(u => !u.TwoFactorEnabled && u.DeletedAtUtc == null
                        && u.Status != UserStatus.Suspended && u.Status != UserStatus.Banned && u.Status != UserStatus.Deleted
                        && u.UserRoles.Any(r => staffRoles.Contains(r.RoleName.ToLower())))
            .OrderBy(u => u.Email)
            .Take(500)
            .ToListAsync(cancellationToken);

        var now = DateTime.UtcNow;
        var recipients = users.Select(u => u.Id.ToString("N")).ToList();
        var withCode = (await _db.OtpCodes.AsNoTracking()
            .Where(o => recipients.Contains(o.Recipient) && o.Type == OtpType.TwoFactorSetupCode && !o.IsConsumed && o.ExpiresAtUtc > now
                        && o.Attempts < OtpCode.MaxVerificationAttempts)
            .Select(o => o.Recipient).ToListAsync(cancellationToken)).ToHashSet();

        return [.. users.Select(u => new StaffAwaitingTwoFactorDto(
            u.Id, u.Email, u.FullName, GetUserRoles(u), withCode.Contains(u.Id.ToString("N"))))];
    }

    public async Task<TwoFactorSetupCodeDto> IssueTwoFactorSetupCodeAsync(
        Guid adminId, Guid userId, string? clientIp = null, DateTime? utcNowOverride = null, CancellationToken cancellationToken = default)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        if (adminId == userId)
        {
            // Otherwise one stolen admin password plus this endpoint would let anyone reset the admin's own authenticator.
            throw new ForbiddenException("SETUP_CODE_FOR_SELF", "Another administrator must issue your setup code.");
        }

        User user = await GetUserByIdOrThrowAsync(userId, cancellationToken);
        if (AccountRules.IsDisabled(user.Status))
        {
            throw new ForbiddenException("ACCOUNT_DISABLED", "This account is not active.");
        }
        if (user.TwoFactorEnabled || !RequiresSecondFactor(user))
        {
            throw new ConflictException("TWO_FACTOR_ENROLLMENT_NOT_NEEDED", "This account does not need to enrol an authenticator.");
        }

        // One transaction: earlier codes are retired only if the new one is stored.
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        await RetireEnrollmentCredentialsAsync(user.Id, cancellationToken);
        string code = NewSetupCode();
        DateTime expires = now.Add(SetupCodeLifetime);
        _db.OtpCodes.Add(new OtpCode(Guid.NewGuid(), user.Id.ToString("N"), _tokenService.HashToken(NormalizeSetupCode(code)),
            OtpType.TwoFactorSetupCode, $"setup_{Guid.NewGuid():N}", expires));
        RecordAudit(adminId, clientIp, "user.2fa_setup_code_issued", "users", user.Id.ToString(), null, new { ExpiresAtUtc = expires });
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return new TwoFactorSetupCodeDto(user.Id, code, expires);
    }

    public async Task<AuthTokenResponseDto> CompleteTwoFactorEnrollmentAsync(
        string enrollmentToken, string code, string? clientIp = null, string? userAgent = null,
        DateTime? utcNowOverride = null, CancellationToken cancellationToken = default)
    {
        var now = utcNowOverride ?? DateTime.UtcNow;
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);

        // Token: "{user id}.{reference}.{secret}"; the first two find the row through the (recipient, reference) index.
        string[] parts = (enrollmentToken ?? string.Empty).Split('.', 3);
        OtpCode? token = parts.Length == 3 && parts[1].StartsWith("enr_", StringComparison.Ordinal)
            ? await LockOtpAsync(parts[0], parts[1], [OtpType.TwoFactorEnrollment], cancellationToken)
            : null;
        if (token is null || !token.CanVerify(now)
            || !CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(token.CodeHash), Encoding.ASCII.GetBytes(_tokenService.HashToken(parts[2]))))
        {
            throw EnrollmentExpired();
        }

        User user = await GetUserByIdOrThrowAsync(Guid.ParseExact(token.Recipient, "N"), cancellationToken);
        if (SessionRefusal(user, now) is { } refusal)
        {
            throw refusal;
        }
        if (user.TwoFactorEnabled || !RequiresSecondFactor(user))
        {
            throw new ConflictException("TWO_FACTOR_ENROLLMENT_NOT_NEEDED", "This account does not need to enrol an authenticator.");
        }

        if (string.IsNullOrWhiteSpace(user.TwoFactorSecret)
            || !_totpService.TryVerifyTotpCode(user.TwoFactorSecret, code ?? string.Empty, new DateTimeOffset(now, TimeSpan.Zero), user.LastUsedTotpTimeStep, out long step))
        {
            // Counts against the token and the account, like a wrong code at sign-in.
            token.IncrementAttempts(now);
            user.RecordFailedLogin(now);
            RecordAudit(user.Id, clientIp, "user.2fa_enrollment_failed", "users", user.Id.ToString(), null, new { token.Attempts });
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            throw token.CanVerify(now)
                ? new ValidationException("code", "That authenticator code is not right. Check the device clock and try the next code.")
                : EnrollmentExpired();
        }

        user.RecordTotpTimeStepUsed(step, now);
        user.EnableTwoFactor(now);
        token.MarkConsumed(now);
        await RetireEnrollmentCredentialsAsync(user.Id, cancellationToken);
        // Sessions begun before the authenticator existed never passed a second factor; only the new one survives.
        await RevokeAllUserSessionsAsync(user.Id, now, cancellationToken);
        user.RecordSuccessfulLogin(now);
        RecordAudit(user.Id, clientIp, "user.2fa_enabled", "users", user.Id.ToString(), null, new { TwoFactorEnabled = true, Via = "enrollment" });

        AuthTokenResponseDto response = IssueSessionTokens(user, Guid.NewGuid(), clientIp, userAgent, now);
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return response;
    }

    private static readonly TimeSpan EnrollmentTokenLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan SetupCodeLifetime = TimeSpan.FromHours(72);
    private const string SetupCodeAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";   // no 0/O or 1/I to misread

    private static UnauthorizedAccessException EnrollmentExpired() =>
        new("This setup has expired. Sign in again with your password and the same setup code (or ask for a new one if it has expired).");

    private static void RequireSelfServiceEnrollment(User user)
    {
        if (AccountRules.RequiresSecondFactor(false, GetUserRoles(user)))
        {
            throw new ForbiddenException("STAFF_ENROLL_WITH_SETUP_CODE",
                "Staff set up their authenticator at sign-in with a setup code from an administrator.");
        }
    }

    /// <summary>Ten characters from a 32-letter alphabet (50 bits) as XXXXX-XXXXX; three tries before it is spent.</summary>
    private static string NewSetupCode()
    {
        var chars = new char[10];
        for (int i = 0; i < chars.Length; i++) chars[i] = SetupCodeAlphabet[RandomNumberGenerator.GetInt32(SetupCodeAlphabet.Length)];
        return $"{new string(chars, 0, 5)}-{new string(chars, 5, 5)}";
    }

    private static string NormalizeSetupCode(string code) =>
        new((code ?? string.Empty).Where(char.IsLetterOrDigit).Select(char.ToUpperInvariant).ToArray());

    /// <summary>Only one way into enrolment is open at a time: a new setup code or token retires every earlier one.</summary>
    private Task RetireEnrollmentCredentialsAsync(Guid userId, CancellationToken cancellationToken, bool tokensOnly = false)
    {
        string recipient = userId.ToString("N");
        string[] types = tokensOnly
            ? [OtpType.TwoFactorEnrollment.ToString()]
            : [OtpType.TwoFactorSetupCode.ToString(), OtpType.TwoFactorEnrollment.ToString()];
        return _db.Database.ExecuteSqlInterpolatedAsync(
            $"UPDATE otp_codes SET is_consumed = TRUE WHERE recipient = {recipient} AND type = ANY({types}) AND is_consumed = FALSE",
            cancellationToken);
    }

    /// <summary>
    /// Password already checked. Spends the administrator's setup code and returns, once, the new authenticator secret, the
    /// recovery codes and a 10-minute token for the first code. A wrong setup code counts against the code and the account.
    /// </summary>
    private async Task<AuthTokenResponseDto> RedeemSetupCodeAsync(User user, string setupCode, string? clientIp, DateTime now, CancellationToken cancellationToken)
    {
        await using var tx = await _db.Database.BeginTransactionAsync(cancellationToken);
        string recipient = user.Id.ToString("N");
        string type = OtpType.TwoFactorSetupCode.ToString();
        OtpCode? setup = (await _db.OtpCodes.FromSqlInterpolated($@"SELECT * FROM otp_codes
                WHERE recipient = {recipient} AND type = {type} AND is_consumed = FALSE
                ORDER BY created_at DESC LIMIT 1 FOR UPDATE")
            .ToListAsync(cancellationToken)).FirstOrDefault();

        if (setup is null || !setup.CanVerify(now)
            || !CryptographicOperations.FixedTimeEquals(
                Encoding.ASCII.GetBytes(setup.CodeHash), Encoding.ASCII.GetBytes(_tokenService.HashToken(NormalizeSetupCode(setupCode)))))
        {
            setup?.IncrementAttempts(now);
            user.RecordFailedLogin(now);
            RecordAudit(user.Id, clientIp, "user.2fa_setup_code_failed", "users", user.Id.ToString(), null, new { setup?.Attempts });
            await _db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            throw new UnauthorizedAccessException("That setup code is not valid. Ask an administrator for a new one.");
        }

        // The setup code stays valid until enrolment succeeds (or it expires / a new one is issued), so someone who runs out
        // of time on the first code can start again without a new code; any earlier token is retired.
        await RetireEnrollmentCredentialsAsync(user.Id, cancellationToken, tokensOnly: true);
        user.ClearFailedLogins(now);
        TotpSetupResponseDto secret = await IssueNewTwoFactorSecretAsync(user, clientIp, now, cancellationToken);

        string reference = $"enr_{Guid.NewGuid():N}";
        string raw = _tokenService.GenerateRawRefreshToken();
        _db.OtpCodes.Add(new OtpCode(Guid.NewGuid(), recipient, _tokenService.HashToken(raw), OtpType.TwoFactorEnrollment, reference,
            now.Add(EnrollmentTokenLifetime)));
        RecordAudit(user.Id, clientIp, "user.2fa_setup_code_redeemed", "users", user.Id.ToString(), null, null);
        await _db.SaveChangesAsync(cancellationToken);
        await tx.CommitAsync(cancellationToken);

        return new AuthTokenResponseDto(string.Empty, 0, "Bearer", MapUserDto(user), null,
            RequiresTwoFactorEnrollment: true, EnrollmentToken: $"{recipient}.{reference}.{raw}", TwoFactorSetup: secret);
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
        CancellationToken cancellationToken = default,
        Guid? currentSessionId = null)
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
        // Without the cookie (a bearer-only client, a blocked cookie) the access token's own session id says which one is current.
        currentFamilyId ??= currentSessionId;

        IQueryable<RefreshToken> targets = _db.RefreshTokens.Where(rt => rt.UserId == userId && !rt.IsRevoked);

        if (request.SessionId.HasValue)
        {
            // A session is a whole token family: naming one of its tokens ends all of them, so no older token keeps it alive.
            Guid sessionId = request.SessionId.Value;
            Guid family = await _db.RefreshTokens.Where(rt => rt.UserId == userId && rt.Id == sessionId)
                .Select(rt => (Guid?)rt.FamilyId).FirstOrDefaultAsync(cancellationToken) ?? sessionId;
            targets = targets.Where(rt => rt.FamilyId == family);
        }
        else if (request.RevokeAllExceptCurrent && currentFamilyId.HasValue)
        {
            Guid family = currentFamilyId.Value;
            targets = targets.Where(rt => rt.FamilyId != family);
        }
        else if (!request.RevokeAll)
        {
            // "This session only": if it cannot be told which one that is, end none rather than all of them.
            Guid? family = currentFamilyId;
            targets = family.HasValue ? targets.Where(rt => rt.FamilyId == family.Value) : targets.Where(_ => false);
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

        string accessToken = _tokenService.GenerateAccessToken(user, roles, permissions, utcNow, familyId);
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
    private static readonly OtpType[] SignInOtpTypes = [OtpType.PhoneVerify, OtpType.EmailVerify, OtpType.LoginOtp];

    private async Task<OtpCode?> LockOtpAsync(string recipient, string referenceId, IReadOnlyCollection<OtpType> types, CancellationToken cancellationToken)
    {
        string[] names = types.Select(t => t.ToString()).ToArray();
        return (await _db.OtpCodes.FromSqlInterpolated($@"SELECT * FROM otp_codes
                WHERE recipient = {recipient} AND reference_id = {referenceId} AND type = ANY({names})
                ORDER BY created_at DESC LIMIT 1 FOR UPDATE")
            .ToListAsync(cancellationToken)).FirstOrDefault();
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

    private static string? _dummyPasswordHash;

    /// <summary>A real hash of a throw-away password, made once, so a missing account still pays the full hashing cost.</summary>
    private string DummyPasswordHash() =>
        _dummyPasswordHash ??= _passwordHasher.HashPassword("Dummy#Password-" + Guid.NewGuid().ToString("N"));

    private async Task<User?> FindUserByIdentifierAsync(string identifier, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(identifier))
        {
            return null;
        }

        string trimmed = identifier.Trim();
        string lowered = trimmed.ToLowerInvariant();
        // Phones are stored in canonical form since F3a.2a; older rows may still hold what was typed.
        string phone = BdMobileNumber.TryNormalize(trimmed, out string e164) ? e164 : trimmed;
        return await _db.Users
            .Include(u => u.UserRoles)
            .FirstOrDefaultAsync(
                u => u.DeletedAtUtc == null && (u.Email == lowered || (u.Phone != null && (u.Phone == phone || u.Phone == trimmed))),
                cancellationToken);
    }

    private const string BdMobileOnlyMessage = "Codes are sent by SMS only to Bangladeshi mobile numbers (+8801XXXXXXXXX).";

    private async Task<User> GetUserByIdOrThrowAsync(Guid userId, CancellationToken cancellationToken) =>
        await _db.Users.Include(u => u.UserRoles).FirstOrDefaultAsync(u => u.Id == userId && u.DeletedAtUtc == null, cancellationToken)
            ?? throw new KeyNotFoundException("User account was not found.");

    private static bool IsUniqueViolation(DbUpdateException ex) =>
        ex.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation };

    /// <summary>Whether a session needs the password and a TOTP or recovery code: the user turned 2FA on, or a role demands it.</summary>
    private static bool RequiresSecondFactor(User user) => AccountRules.RequiresSecondFactor(user.TwoFactorEnabled, GetUserRoles(user));

    /// <summary>Why this account may not start a session right now (suspended, banned, locked), or null when it may.</summary>
    private static Exception? SessionRefusal(User user, DateTime utcNow) =>
        AccountRules.IsDisabled(user.Status)
            ? new ForbiddenException("ACCOUNT_DISABLED", "This account is not active.")
            : user.IsLockedOut(utcNow)
                ? new UnauthorizedAccessException($"Account is temporarily locked due to multiple failed login attempts until {user.LockoutUntilUtc:O}.")
                : null;

    private static ForbiddenException PasswordAndSecondFactorRequired() =>
        new("PASSWORD_AND_2FA_REQUIRED", "This account signs in with its password and an authenticator code.");

    private static IReadOnlyList<string> GetUserRoles(User user) =>
        AccountRules.EffectiveRoles(user.UserRoles.Select(ur => ur.RoleName));

    private static IReadOnlyList<string> GetUserPermissions(IReadOnlyList<string> roles) => AccountRules.PermissionsFor(roles);

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
