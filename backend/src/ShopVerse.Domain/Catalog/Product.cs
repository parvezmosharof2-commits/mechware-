using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.Domain.Catalog;

public enum ProductStatus { Draft = 1, PendingReview = 2, Active = 3, Rejected = 4, Archived = 5 }

public enum ProductCondition { BrandNew = 1, Refurbished = 2, Used = 3 }

/// <summary>
/// Product aggregate root. Lifecycle: Draft → PendingReview → Active | Rejected → Archived.
/// A rejected product returns to Draft for correction; an archived product can be restored to Draft.
/// </summary>
public sealed class Product : AggregateRoot<Guid>
{
    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductMedia> _media = [];
    private readonly List<ProductAttributeValue> _attributeValues = [];

    public Guid SellerId { get; private set; }
    public Guid CategoryId { get; private set; }
    public Guid? BrandId { get; private set; }
    public string TitleEn { get; private set; } = string.Empty;
    public string TitleBn { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string ShortDescriptionEn { get; private set; } = string.Empty;
    public string ShortDescriptionBn { get; private set; } = string.Empty;
    public string DescriptionHtml { get; private set; } = string.Empty;

    /// <summary>JSON array of bullet strings (JSONB).</summary>
    public string HighlightsJson { get; private set; } = "[]";

    /// <summary>JSON object of grouped specs: <c>{ "Electrical": { "Voltage": "24 V DC" } }</c> (JSONB).</summary>
    public string SpecsJson { get; private set; } = "{}";

    public string? DatasheetUrl { get; private set; }
    public string? CadModelUrl { get; private set; }
    public ProductStatus Status { get; private set; } = ProductStatus.Draft;
    public string? RejectionReason { get; private set; }
    public ProductCondition Condition { get; private set; } = ProductCondition.BrandNew;
    public int WarrantyMonths { get; private set; } = 12;
    public int ReturnPolicyDays { get; private set; } = 7;
    public decimal WeightKg { get; private set; } = 0.5m;
    public decimal LengthCm { get; private set; } = 10m;
    public decimal WidthCm { get; private set; } = 10m;
    public decimal HeightCm { get; private set; } = 10m;
    public decimal RatingAvg { get; private set; }
    public int RatingCount { get; private set; }
    public int SoldCount { get; private set; }
    public DateTime? DeletedAtUtc { get; private set; }

    public IReadOnlyCollection<ProductVariant> Variants => _variants.AsReadOnly();
    public IReadOnlyCollection<ProductMedia> Media => _media.AsReadOnly();
    public IReadOnlyCollection<ProductAttributeValue> AttributeValues => _attributeValues.AsReadOnly();

    private Product() { }

    public Product(
        Guid id, Guid sellerId, Guid categoryId, Guid? brandId, string titleEn, string titleBn, string slug,
        string shortDescriptionEn, string shortDescriptionBn, string descriptionHtml) : base(id)
    {
        if (string.IsNullOrWhiteSpace(titleEn)) throw new ValidationException("titleEn", "English title is required.");
        if (string.IsNullOrWhiteSpace(titleBn)) throw new ValidationException("titleBn", "Bengali title is required.");

        SellerId = sellerId;
        CategoryId = categoryId;
        BrandId = brandId;
        TitleEn = titleEn.Trim();
        TitleBn = titleBn.Trim();
        Slug = Catalog.Slug.Require(slug);
        ShortDescriptionEn = shortDescriptionEn;
        ShortDescriptionBn = shortDescriptionBn;
        DescriptionHtml = descriptionHtml;
    }

    #region Content

    public void SetContent(string highlightsJson, string specsJson, string? datasheetUrl, string? cadModelUrl)
    {
        EnsureEditable();
        HighlightsJson = string.IsNullOrWhiteSpace(highlightsJson) ? "[]" : highlightsJson;
        SpecsJson = string.IsNullOrWhiteSpace(specsJson) ? "{}" : specsJson;
        DatasheetUrl = datasheetUrl;
        CadModelUrl = cadModelUrl;
        MarkUpdated();
    }

    public void SetLogistics(ProductCondition condition, int warrantyMonths, int returnPolicyDays,
        decimal weightKg, decimal lengthCm, decimal widthCm, decimal heightCm)
    {
        EnsureEditable();
        if (warrantyMonths < 0) throw new ValidationException("warrantyMonths", "Warranty cannot be negative.");
        if (returnPolicyDays < 0) throw new ValidationException("returnPolicyDays", "Return window cannot be negative.");
        if (weightKg <= 0 || lengthCm <= 0 || widthCm <= 0 || heightCm <= 0)
            throw new ValidationException("dimensions", "Weight and dimensions must be positive.");

        Condition = condition;
        WarrantyMonths = warrantyMonths;
        ReturnPolicyDays = returnPolicyDays;
        WeightKg = weightKg;
        LengthCm = lengthCm;
        WidthCm = widthCm;
        HeightCm = heightCm;
        MarkUpdated();
    }

    public void Recategorize(Guid categoryId, Guid? brandId)
    {
        EnsureEditable();
        CategoryId = categoryId;
        BrandId = brandId;
        MarkUpdated();
    }

    /// <summary>Applies aggregated review statistics (maintained by the review module).</summary>
    public void ApplyRatingSummary(decimal ratingAvg, int ratingCount)
    {
        if (ratingAvg is < 0m or > 5m) throw new ValidationException("ratingAvg", "Rating must be between 0 and 5.");
        RatingAvg = decimal.Round(ratingAvg, 2, MidpointRounding.ToEven);
        RatingCount = ratingCount;
    }

    public void ApplySoldCount(int soldCount)
    {
        if (soldCount < 0) throw new ValidationException("soldCount", "Sold count cannot be negative.");
        SoldCount = soldCount;
    }

    #endregion

    #region Variants & media

    public ProductVariant AddVariant(
        Guid variantId, string sku, string? barcode, IDictionary<string, string> optionValues, Money price,
        Money? compareAtPrice, Money? costPrice, decimal weightKg, bool isDefault)
    {
        EnsureEditable();
        if (_variants.Any(v => string.Equals(v.Sku, sku, StringComparison.OrdinalIgnoreCase)))
            throw new ValidationException("sku", $"SKU '{sku}' already exists on this product.");

        var optionsKey = ProductVariant.ComputeOptionKey(optionValues);
        if (_variants.Any(v => v.OptionKey == optionsKey))
            throw new ValidationException("optionValues", "A variant with the same option combination already exists.");

        var variant = new ProductVariant(variantId, Id, sku, barcode, optionValues, price, compareAtPrice, costPrice,
            weightKg, isDefault || _variants.Count == 0);

        if (variant.IsDefault)
        {
            foreach (var existing in _variants) existing.ClearDefault();
        }

        _variants.Add(variant);
        MarkUpdated();
        return variant;
    }

    public void AddMedia(Guid mediaId, Guid? variantId, string url, string altText, int width, int height,
        string? blurDataUrl = null, bool isVideo = false)
    {
        EnsureEditable();
        _media.Add(new ProductMedia(mediaId, Id, variantId, url, altText, isVideo ? MediaType.Video : MediaType.Image,
            _media.Count, width, height, blurDataUrl));
        MarkUpdated();
    }

    public void AssignAttributeValue(Guid attributeValueId)
    {
        EnsureEditable();
        if (_attributeValues.All(a => a.AttributeValueId != attributeValueId))
        {
            _attributeValues.Add(new ProductAttributeValue(Id, attributeValueId));
            MarkUpdated();
        }
    }

    /// <summary>
    /// Builds the cartesian product of option axes into variants, e.g. Color × Storage.
    /// SKUs are <c>{skuPrefix}-{OPT1}-{OPT2}</c>; the first combination becomes the default variant.
    /// Combinations that already exist are skipped, so the call is idempotent.
    /// </summary>
    public IReadOnlyList<ProductVariant> GenerateVariants(
        string skuPrefix, IReadOnlyDictionary<string, IReadOnlyList<string>> axes, Money basePrice,
        Func<IReadOnlyDictionary<string, string>, Money>? priceResolver = null, decimal? weightKg = null)
    {
        EnsureEditable();
        if (axes.Count == 0) throw new ValidationException("axes", "At least one option axis is required.");
        if (axes.Any(a => a.Value.Count == 0)) throw new ValidationException("axes", "Every option axis needs at least one value.");

        var created = new List<ProductVariant>();
        foreach (var combo in VariantGenerator.Combinations(axes))
        {
            var key = ProductVariant.ComputeOptionKey(combo);
            if (_variants.Any(v => v.OptionKey == key)) continue;

            var sku = VariantGenerator.BuildSku(skuPrefix, combo.Values);
            var price = priceResolver?.Invoke(combo) ?? basePrice;
            created.Add(AddVariant(Guid.NewGuid(), sku, null, combo, price, null, null, weightKg ?? WeightKg, false));
        }

        return created;
    }

    #endregion

    #region Slug

    /// <summary>Changes the slug; returns the retired <see cref="SlugHistory"/> (null when unchanged).</summary>
    public SlugHistory? ChangeSlug(string newSlug, Guid historyId)
    {
        var valid = Catalog.Slug.Require(newSlug);
        if (valid == Slug) return null;

        var history = new SlugHistory(historyId, SlugEntityType.Product, Id, Slug);
        var old = Slug;
        Slug = valid;
        AddDomainEvent(new ProductSlugChangedEvent(Id, old, valid));
        MarkUpdated();
        return history;
    }

    #endregion

    #region Lifecycle

    public void SubmitForReview()
    {
        Transition(ProductStatus.Draft, ProductStatus.PendingReview);
        if (_variants.Count == 0) { Status = ProductStatus.Draft; throw new ValidationException("variants", "A product needs at least one variant before review."); }
        if (_media.Count == 0) { Status = ProductStatus.Draft; throw new ValidationException("media", "A product needs at least one image before review."); }
    }

    public void Approve()
    {
        Transition(ProductStatus.PendingReview, ProductStatus.Active);
        RejectionReason = null;
    }

    public void Reject(string reason)
    {
        if (string.IsNullOrWhiteSpace(reason)) throw new ValidationException("reason", "A rejection reason is required.");
        Transition(ProductStatus.PendingReview, ProductStatus.Rejected);
        RejectionReason = reason.Trim();
    }

    /// <summary>Rejected or archived products return to Draft so they can be corrected and resubmitted.</summary>
    public void ReturnToDraft()
    {
        if (Status is not (ProductStatus.Rejected or ProductStatus.Archived))
            throw new InvalidProductTransitionException(Status, ProductStatus.Draft);
        Status = ProductStatus.Draft;
        MarkUpdated();
    }

    public void Archive()
    {
        if (Status == ProductStatus.Archived) throw new InvalidProductTransitionException(Status, ProductStatus.Archived);
        if (Status == ProductStatus.PendingReview) throw new InvalidProductTransitionException(Status, ProductStatus.Archived);
        Status = ProductStatus.Archived;
        MarkUpdated();
    }

    /// <summary>Queues a <see cref="ProductUpdatedEvent"/> without changing state (admin "reindex", bulk imports).</summary>
    public void RequestReindex() => AddDomainEvent(new ProductUpdatedEvent(Id, Slug, Status));

    public void SoftDelete(DateTime nowUtc)
    {
        DeletedAtUtc = nowUtc;
        Status = ProductStatus.Archived;
        MarkUpdated();
    }

    private void Transition(ProductStatus expectedFrom, ProductStatus to)
    {
        if (Status != expectedFrom) throw new InvalidProductTransitionException(Status, to);
        Status = to;
        MarkUpdated();
    }

    private void EnsureEditable()
    {
        if (Status is ProductStatus.PendingReview)
            throw new ValidationException("status", "A product under review cannot be edited.");
    }

    #endregion

    private void MarkUpdated()
    {
        UpdatedAtUtc = DateTime.UtcNow;
        AddDomainEvent(new ProductUpdatedEvent(Id, Slug, Status));
    }
}

public sealed class InvalidProductTransitionException : DomainException
{
    public InvalidProductTransitionException(ProductStatus from, ProductStatus to)
        : base("INVALID_PRODUCT_TRANSITION", $"Product cannot move from {from} to {to}.") { }
}
