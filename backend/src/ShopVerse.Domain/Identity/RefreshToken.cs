using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Identity;

/// <summary>
/// Represents a rotating refresh token with family tracking and 10-second multi-tab grace window (ADR-0006).
/// </summary>
[NotAudited]
public sealed class RefreshToken : Entity<Guid>
{
    public Guid UserId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public Guid FamilyId { get; private set; }
    public bool IsUsed { get; private set; }
    public bool IsRevoked { get; private set; }
    public DateTime? UsedAtUtc { get; private set; }
    public string? ReplacedByTokenHash { get; private set; }
    public string? CreatedByIp { get; private set; }
    public string? UserAgent { get; private set; }
    public DateTime ExpiresAtUtc { get; private set; }

    private RefreshToken() { }

    public RefreshToken(
        Guid id,
        Guid userId,
        string tokenHash,
        Guid familyId,
        string? createdByIp,
        string? userAgent,
        DateTime expiresAtUtc) : base(id)
    {
        UserId = userId;
        TokenHash = tokenHash;
        FamilyId = familyId;
        CreatedByIp = createdByIp;
        UserAgent = userAgent;
        ExpiresAtUtc = expiresAtUtc;
    }

    public bool IsExpired(DateTime utcNow) => utcNow >= ExpiresAtUtc;

    public bool IsActive(DateTime utcNow) => !IsUsed && !IsRevoked && !IsExpired(utcNow);

    /// <summary>
    /// ADR-0006: Tolerates concurrent Next.js SSR + browser tab refresh collisions within 10 seconds
    /// when originating from the same client IP address.
    /// </summary>
    public bool IsWithinGraceWindow(DateTime utcNow, string? requestIp, int graceSeconds = 10)
    {
        if (!IsUsed || IsRevoked || IsExpired(utcNow) || !UsedAtUtc.HasValue)
        {
            return false;
        }

        bool sameIp = string.IsNullOrEmpty(CreatedByIp) ||
                      string.Equals(CreatedByIp, requestIp, StringComparison.OrdinalIgnoreCase);

        if (!sameIp)
        {
            return false;
        }

        double elapsedSeconds = (utcNow - UsedAtUtc.Value).TotalSeconds;
        return elapsedSeconds >= 0 && elapsedSeconds <= graceSeconds;
    }

    public void MarkUsed(string replacedByTokenHash, DateTime utcNow)
    {
        IsUsed = true;
        UsedAtUtc = utcNow;
        ReplacedByTokenHash = replacedByTokenHash;
        UpdatedAtUtc = utcNow;
    }

    public void Revoke(DateTime utcNow)
    {
        IsRevoked = true;
        UpdatedAtUtc = utcNow;
    }
}
