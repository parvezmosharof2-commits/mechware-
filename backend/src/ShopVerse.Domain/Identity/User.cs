using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Identity;

public enum UserStatus
{
    PendingVerification = 0,
    Active = 1,
    Suspended = 2,
    Banned = 3,
    Deleted = 4
}

/// <summary>
/// Aggregate root representing a MECHWARE platform user (Buyer, Seller, Staff, or Admin).
/// </summary>
public sealed class User : AggregateRoot<Guid>
{
    private readonly List<UserRole> _userRoles = [];
    private readonly List<Address> _addresses = [];

    public string Email { get; private set; } = string.Empty;
    public string? Phone { get; private set; }
    public string? PasswordHash { get; private set; }
    public string FullName { get; private set; } = string.Empty;
    public string? AvatarUrl { get; private set; }
    public string Locale { get; private set; } = "en";
    public UserStatus Status { get; private set; } = UserStatus.PendingVerification;

    public bool TwoFactorEnabled { get; private set; }
    public string? TwoFactorSecret { get; private set; }
    public string? TwoFactorRecoveryCodesJson { get; private set; }

    public string? GoogleSubjectId { get; private set; }
    public Guid? SellerId { get; private set; }

    public int FailedLoginAttempts { get; private set; }
    public DateTime? LockoutUntilUtc { get; private set; }
    public DateTime? EmailVerifiedAtUtc { get; private set; }
    public DateTime? PhoneVerifiedAtUtc { get; private set; }
    public DateTime? LastLoginAtUtc { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    public IReadOnlyCollection<UserRole> UserRoles => _userRoles.AsReadOnly();
    public IReadOnlyCollection<Address> Addresses => _addresses.AsReadOnly();

    private User() { }

    private User(
        Guid id,
        string email,
        string? phone,
        string? passwordHash,
        string fullName,
        string locale,
        Guid? sellerId = null) : base(id)
    {
        Email = email.Trim().ToLowerInvariant();
        Phone = string.IsNullOrWhiteSpace(phone) ? null : phone.Trim();
        PasswordHash = passwordHash;
        FullName = fullName.Trim();
        Locale = string.IsNullOrWhiteSpace(locale) ? "en" : locale.Trim().ToLowerInvariant();
        SellerId = sellerId;
        Status = UserStatus.PendingVerification;
    }

    public static User Create(
        string email,
        string? phone,
        string? passwordHash,
        string fullName,
        string locale = "en",
        Guid? sellerId = null)
    {
        if (string.IsNullOrWhiteSpace(email) || !email.Contains('@'))
        {
            throw new ValidationException("email", "A valid email address is required.");
        }

        if (string.IsNullOrWhiteSpace(fullName) || fullName.Trim().Length < 2)
        {
            throw new ValidationException("fullName", "Full name must be at least 2 characters.");
        }

        return new User(Guid.NewGuid(), email, phone, passwordHash, fullName, locale, sellerId);
    }

    public bool IsLockedOut(DateTime utcNow) =>
        LockoutUntilUtc.HasValue && LockoutUntilUtc.Value > utcNow;

    /// <summary>
    /// Progressive brute-force protection: locks account after reaching maxAttempts,
    /// escalating lockout duration on repeated failures.
    /// </summary>
    public void RecordFailedLogin(DateTime utcNow, int maxAttempts = 5)
    {
        FailedLoginAttempts++;
        UpdatedAtUtc = utcNow;

        if (FailedLoginAttempts >= maxAttempts)
        {
            int overflow = FailedLoginAttempts - maxAttempts;
            int lockoutMinutes = overflow switch
            {
                0 => 5,
                1 => 10,
                _ => 15
            };
            LockoutUntilUtc = utcNow.AddMinutes(lockoutMinutes);
        }
    }

    public void RecordSuccessfulLogin(DateTime utcNow)
    {
        FailedLoginAttempts = 0;
        LockoutUntilUtc = null;
        LastLoginAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
    }

    public void VerifyEmail(DateTime utcNow)
    {
        EmailVerifiedAtUtc = utcNow;
        if (Status == UserStatus.PendingVerification)
        {
            Status = UserStatus.Active;
        }
        UpdatedAtUtc = utcNow;
    }

    public void VerifyPhone(DateTime utcNow)
    {
        PhoneVerifiedAtUtc = utcNow;
        if (Status == UserStatus.PendingVerification)
        {
            Status = UserStatus.Active;
        }
        UpdatedAtUtc = utcNow;
    }

    public void LinkGoogleAccount(string googleSubjectId, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(googleSubjectId))
        {
            throw new ValidationException("googleSubjectId", "Google Subject ID cannot be empty.");
        }

        GoogleSubjectId = googleSubjectId;
        EmailVerifiedAtUtc ??= utcNow;
        if (Status == UserStatus.PendingVerification)
        {
            Status = UserStatus.Active;
        }
        UpdatedAtUtc = utcNow;
    }

    public void UpdatePasswordHash(string newPasswordHash, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(newPasswordHash))
        {
            throw new ValidationException("passwordHash", "Password hash cannot be empty.");
        }

        PasswordHash = newPasswordHash;
        FailedLoginAttempts = 0;
        LockoutUntilUtc = null;
        UpdatedAtUtc = utcNow;
    }

    public void ConfigureTwoFactorSecret(string base32Secret, string recoveryCodesJson, DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(base32Secret))
        {
            throw new ValidationException("twoFactorSecret", "TOTP secret is required.");
        }

        TwoFactorSecret = base32Secret;
        TwoFactorRecoveryCodesJson = recoveryCodesJson;
        UpdatedAtUtc = utcNow;
    }

    public void EnableTwoFactor(DateTime utcNow)
    {
        if (string.IsNullOrWhiteSpace(TwoFactorSecret))
        {
            throw new ValidationException("twoFactorSecret", "Cannot enable 2FA before configuring a TOTP secret.");
        }

        TwoFactorEnabled = true;
        UpdatedAtUtc = utcNow;
    }

    public void UpdateRecoveryCodesJson(string recoveryCodesJson, DateTime utcNow)
    {
        TwoFactorRecoveryCodesJson = recoveryCodesJson;
        UpdatedAtUtc = utcNow;
    }

    public void AssignSellerId(Guid sellerId, DateTime utcNow)
    {
        SellerId = sellerId;
        UpdatedAtUtc = utcNow;
    }

    public void AssignRole(Guid roleId, string roleName)
    {
        if (_userRoles.Any(ur => ur.RoleId == roleId || string.Equals(ur.RoleName, roleName, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        _userRoles.Add(new UserRole(Id, roleId, roleName.ToLowerInvariant()));
    }

    public void UpdateProfile(string fullName, string? locale, string? avatarUrl, DateTime utcNow)
    {
        if (!string.IsNullOrWhiteSpace(fullName))
        {
            FullName = fullName.Trim();
        }

        if (!string.IsNullOrWhiteSpace(locale))
        {
            Locale = locale.Trim().ToLowerInvariant();
        }

        AvatarUrl = avatarUrl;
        UpdatedAtUtc = utcNow;
    }

    /// <summary>
    /// GDPR Right-to-Erasure: scrubs all PII while preserving referential integrity for financial ledgers.
    /// </summary>
    public void AnonymizeForGdpr(DateTime utcNow)
    {
        Email = $"deleted-{Id:N}@anonymized.mechware.com.bd";
        Phone = null;
        PasswordHash = null;
        FullName = "Deleted User";
        AvatarUrl = null;
        GoogleSubjectId = null;
        TwoFactorEnabled = false;
        TwoFactorSecret = null;
        TwoFactorRecoveryCodesJson = null;
        Status = UserStatus.Deleted;
        DeletedAtUtc = utcNow;
        UpdatedAtUtc = utcNow;
        _addresses.Clear();
    }
}
