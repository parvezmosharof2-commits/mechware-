using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Identity;

public enum OtpType
{
    PhoneVerify = 0,
    EmailVerify = 1,
    PasswordReset = 2,
    LoginOtp = 3
}

/// <summary>
/// Bound one-time passcode entity (MW-P3-02 & AUD-P3-06: binds Recipient + Type + ReferenceId + max 3 attempts per DATA_MODEL.md).
/// </summary>
[NotAudited]
public sealed class OtpCode : Entity<Guid>
{
    public const int MaxVerificationAttempts = 3;

    public string Recipient { get; private set; } = string.Empty;
    public string CodeHash { get; private set; } = string.Empty;
    public OtpType Type { get; private set; }
    public string ReferenceId { get; private set; } = string.Empty;
    public int Attempts { get; private set; }
    public bool IsConsumed { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    private OtpCode() { }

    public OtpCode(
        Guid id,
        string recipient,
        string codeHash,
        OtpType type,
        string referenceId,
        DateTime expiresAtUtc) : base(id)
    {
        Recipient = recipient.Trim().ToLowerInvariant();
        CodeHash = codeHash;
        Type = type;
        ReferenceId = referenceId;
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool CanVerify(DateTime utcNow) =>
        !IsConsumed && Attempts < MaxVerificationAttempts && utcNow < ExpiresAtUtc;

    public void IncrementAttempts(DateTime utcNow)
    {
        Attempts++;
        if (Attempts >= MaxVerificationAttempts)
        {
            IsConsumed = true;
        }
        UpdatedAtUtc = utcNow;
    }

    public void MarkConsumed(DateTime utcNow)
    {
        IsConsumed = true;
        UpdatedAtUtc = utcNow;
    }
}
