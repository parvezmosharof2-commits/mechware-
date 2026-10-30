using ShopVerse.Domain.Common;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Sellers;

public enum SellerStatus { Pending = 1, Approved = 2, Rejected = 3, Suspended = 4 }

/// <summary>
/// Multi-vendor merchant profile (DATA_MODEL §2.2). Phase 4 only needs the storefront-facing
/// fields; KYC and payout details are extended in Phase 10.
/// </summary>
public sealed class Seller : Entity<Guid>
{
    public Guid UserId { get; private set; }
    public string StoreName { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string? LogoUrl { get; private set; }
    public string? BannerUrl { get; private set; }
    public string? Description { get; private set; }
    public SellerStatus Status { get; private set; } = SellerStatus.Pending;
    public decimal? CommissionOverrideRate { get; private set; }
    public int ApprovedListingsCount { get; private set; }
    public decimal RatingAvg { get; private set; }
    public int RatingCount { get; private set; }
    public decimal ResponseRatePercent { get; private set; } = 100m;
    public DateTime JoinedAtUtc { get; private set; } = DateTime.UtcNow;
    public DateTime? ApprovedAtUtc { get; private set; }

    private Seller() { }

    public Seller(Guid id, Guid userId, string storeName, string slug, string? logoUrl, string? bannerUrl,
        string? description, decimal ratingAvg, int ratingCount, decimal responseRatePercent, DateTime joinedAtUtc) : base(id)
    {
        if (string.IsNullOrWhiteSpace(storeName)) throw new ValidationException("storeName", "Store name is required.");
        if (ratingAvg is < 0m or > 5m) throw new ValidationException("ratingAvg", "Rating must be between 0 and 5.");

        UserId = userId;
        StoreName = storeName.Trim();
        Slug = ShopVerse.Domain.Catalog.Slug.Require(slug);
        LogoUrl = SafeUrl.RequireOptional(logoUrl, "logoUrl");
        BannerUrl = SafeUrl.RequireOptional(bannerUrl, "bannerUrl");
        Description = description;
        RatingAvg = ratingAvg;
        RatingCount = ratingCount;
        ResponseRatePercent = responseRatePercent;
        JoinedAtUtc = joinedAtUtc;
    }

    public void Approve(DateTime nowUtc)
    {
        Status = SellerStatus.Approved;
        ApprovedAtUtc = nowUtc;
        UpdatedAtUtc = nowUtc;
    }
}
