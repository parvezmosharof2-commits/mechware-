using System.Linq.Expressions;
using ShopVerse.Domain.Common;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;
using ShopVerse.Domain.Common.ValueObjects;

namespace ShopVerse.Domain.Catalog;

public enum ProductStatus { Draft = 1, PendingReview = 2, Active = 3, Rejected = 4, Archived = 5 }

public enum ProductCondition { BrandNew = 1, Refurbished = 2, Used = 3 }

/// <summary>
/// Whether the manufacturer still supplies the part (PIM): <see cref="Nrnd"/> is "not recommended for new designs" (still
/// sold, but a successor should be used for new work); <see cref="Obsolete"/> is end of life. Separate from
/// <see cref="ProductStatus"/>, which is about publishing on MECHWARE.
/// </summary>
public enum SupplyStatus { Active = 1, Nrnd = 2, Obsolete = 3 }

/// <summary>
/// Product aggregate root. Lifecycle: Draft → PendingReview → Active | Rejected → Archived.
/// A rejected product returns to Draft for correction; an archived product can be restored to Draft.
/// </summary>
public sealed class Product : AggregateRoot<Guid>
{
    private readonly List<ProductVariant> _variants = [];
    private readonly List<ProductMedia> _media = [];
    private readonly List<ProductAttributeValue> _attributeValues = [];
    private readonly List<ProductAlternative> _alternatives = [];

    /// <summary>At most this many alternatives per product: a product page lists them, it is not a cross-reference database.</summary>
    public const int MaxAlternatives = 10;

    public const int MaxSuccessorNoteLength = 500;

    /// <summary>
    /// Whether a buyer can be sent to this product: published, not deleted and not obsolete. One definition for every place
    /// that links to another product (successor, alternatives), usable inside a query.
    /// </summary>
    public static readonly Expression<Func<Product, bool>> IsLinkable = p =>
        p.Status == ProductStatus.Active && p.DeletedAtUtc == null && p.SupplyStatus != SupplyStatus.Obsolete;

    private static readonly Func<Product, bool> LinkableNow = IsLinkable.Compile();

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
    public SupplyStatus SupplyStatus { get; private set; } = SupplyStatus.Active;
    /// <summary>The product that replaces this one; only for a product that is not <see cref="SupplyStatus.Active"/>.</summary>
    public Guid? SuccessorProductId { get; private set; }

    /// <summary>True when the successor fits without changes (wiring, mounting, program). Buyers replacing a part in a running
    /// machine need to know before they order (discovery, 2026-11-06).</summary>
    public bool SuccessorIsDropIn { get; private set; }

    /// <summary>What changes when moving to the successor, e.g. "firmware V4: migrate the TIA Portal project".</summary>
    public string? SuccessorNote { get; private set; }

    public IReadOnlyCollection<ProductVariant> Variants => _variants.AsReadOnly();
    public IReadOnlyCollection<ProductMedia> Media => _media.AsReadOnly();
    public IReadOnlyCollection<ProductAttributeValue> AttributeValues => _attributeValues.AsReadOnly();
    public IReadOnlyCollection<ProductAlternative> Alternatives => _alternatives.AsReadOnly();

    /// <summary>Whether a buyer can be sent to this product (see <see cref="IsLinkable"/>).</summary>
    [System.ComponentModel.DataAnnotations.Schema.NotMapped]
    public bool CanBeLinkedTo => LinkableNow(this);

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
        DescriptionHtml = RichTextSanitizer.Sanitize(descriptionHtml);
    }

    #region Content

    public void SetContent(string highlightsJson, string specsJson, string? datasheetUrl, string? cadModelUrl)
    {
        EnsureEditable();
        HighlightsJson = string.IsNullOrWhiteSpace(highlightsJson) ? "[]" : highlightsJson;
        SpecsJson = string.IsNullOrWhiteSpace(specsJson) ? "{}" : specsJson;
        DatasheetUrl = SafeUrl.RequireOptional(datasheetUrl, "datasheetUrl");
        CadModelUrl = SafeUrl.RequireOptional(cadModelUrl, "cadModelUrl");
        MarkUpdated();
    }

    /// <summary>Replaces the rich-text description; HTML is sanitized against an allow-list.</summary>
    public void SetDescription(string shortEn, string shortBn, string descriptionHtml)
    {
        EnsureEditable();
        ShortDescriptionEn = shortEn;
        ShortDescriptionBn = shortBn;
        DescriptionHtml = RichTextSanitizer.Sanitize(descriptionHtml);
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

    /// <summary>
    /// Sets the supply status and, for a product at or near end of life, the product that replaces it. The successor must
    /// be another of the same seller's products, published and itself in supply, so a buyer is never sent to a dead end or
    /// to a different seller's catalogue. Back in supply, the successor is cleared. <paramref name="successorIsDropIn"/> and
    /// <paramref name="successorNote"/> tell the buyer whether the successor fits as it is or what changes; the successor is
    /// only shown, never swapped into an order.
    /// Leaving them out (null) keeps what is stored while the successor stays the same one, so a call that only changes the
    /// status does not erase the note; a different successor starts without a note. A blank note clears it.
    /// </summary>
    public void SetSupply(SupplyStatus status, Product? successor, bool? successorIsDropIn = null, string? successorNote = null)
    {
        EnsureEditable();
        if (!Enum.IsDefined(status)) throw new ValidationException("supplyStatus", "Unknown supply status.");
        if (successor is null && (successorIsDropIn == true || !string.IsNullOrWhiteSpace(successorNote)))
            throw new ValidationException("successorProductId", "Compatibility notes need a successor.");
        bool sameSuccessor = successor is not null && successor.Id == SuccessorProductId;
        bool dropIn = successor is not null && (successorIsDropIn ?? (sameSuccessor && SuccessorIsDropIn));
        string? note = successorNote is null
            ? (sameSuccessor ? SuccessorNote : null)
            : (string.IsNullOrWhiteSpace(successorNote) ? null : successorNote.Trim());
        if (note is { Length: > MaxSuccessorNoteLength })
            throw new ValidationException("successorNote", $"The note can be at most {MaxSuccessorNoteLength} characters.");
        if (successor is not null)
        {
            if (status == SupplyStatus.Active)
                throw new ValidationException("successorProductId", "Only a product that is being phased out can name a successor.");
            if (successor.Id == Id)
                throw new ValidationException("successorProductId", "A product cannot be its own successor.");
            if (successor.SellerId != SellerId)
                throw new ValidationException("successorProductId", "The successor must be a product of the same seller.");
            if (successor.Status != ProductStatus.Active || successor.DeletedAtUtc is not null || successor.SupplyStatus != SupplyStatus.Active)
                throw new ValidationException("successorProductId", "The successor must be published and still in supply.");
        }

        SupplyStatus = status;
        SuccessorProductId = successor?.Id; // never set for an Active product: refused above
        SuccessorIsDropIn = dropIn;
        SuccessorNote = note;
        MarkUpdated();   // raises ProductUpdatedEvent, which evicts the cached product page
    }

    /// <summary>Sets one SKU's order units (see <see cref="ProductVariant.SetOrderUnits"/>) and refreshes the product page.</summary>
    public void SetOrderUnits(Guid variantId, int minOrderQty, int packSize)
    {
        EnsureEditable();
        var variant = _variants.FirstOrDefault(v => v.Id == variantId)
                      ?? throw new ValidationException("variantId", "No such SKU on this product.");
        variant.SetOrderUnits(minOrderQty, packSize);
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

    #region Alternatives

    /// <summary>
    /// Adds a part that can be used instead (same function, possibly another brand or seller). It must be published, so the
    /// list never leads to a dead end; adding it twice changes nothing. One direction only: a newer part often replaces an
    /// older one but not the reverse.
    /// </summary>
    public void AddAlternative(Product alternative)
    {
        EnsureEditable();
        if (alternative.Id == Id) throw new ValidationException("alternativeProductId", "A product cannot be its own alternative.");
        if (!LinkableNow(alternative))
            throw new ValidationException("alternativeProductId", "An alternative must be a published product that is not obsolete.");
        if (_alternatives.Any(a => a.AlternativeProductId == alternative.Id)) return;
        if (_alternatives.Count >= MaxAlternatives)
            throw new ValidationException("alternativeProductId", $"A product can list at most {MaxAlternatives} alternatives.");
        _alternatives.Add(new ProductAlternative(Id, alternative.Id, _alternatives.Count));
        MarkUpdated();
    }

    /// <summary>
    /// Sets the whole list of alternatives, in the given order (staff edit it as one list). Every entry must be linkable and
    /// the list has no duplicates and at most <see cref="MaxAlternatives"/> entries. An entry already present keeps its row.
    /// </summary>
    public void ReplaceAlternatives(IReadOnlyList<Product> ordered)
    {
        EnsureEditable();
        if (ordered.Count > MaxAlternatives)
            throw new ValidationException("alternativeProductIds", $"A product can list at most {MaxAlternatives} alternatives.");
        if (ordered.Select(p => p.Id).Distinct().Count() != ordered.Count)
            throw new ValidationException("alternativeProductIds", "An alternative is listed twice.");
        foreach (var alternative in ordered)
        {
            if (alternative.Id == Id) throw new ValidationException("alternativeProductIds", "A product cannot be its own alternative.");
            if (!LinkableNow(alternative))
                throw new ValidationException("alternativeProductIds", "An alternative must be a published product that is not obsolete.");
        }

        var wanted = ordered.Select(p => p.Id).ToList();
        if (_alternatives.OrderBy(a => a.Position).Select(a => a.AlternativeProductId).SequenceEqual(wanted)) return;

        var existing = _alternatives.ToDictionary(a => a.AlternativeProductId);
        _alternatives.RemoveAll(a => !wanted.Contains(a.AlternativeProductId));
        for (int i = 0; i < wanted.Count; i++)
        {
            if (existing.TryGetValue(wanted[i], out var row)) row.MoveTo(i);
            else _alternatives.Add(new ProductAlternative(Id, wanted[i], i));
        }

        _alternatives.Sort((x, y) => x.Position.CompareTo(y.Position));
        MarkUpdated();
    }

    public void RemoveAlternative(Guid alternativeProductId)
    {
        EnsureEditable();
        if (_alternatives.RemoveAll(a => a.AlternativeProductId == alternativeProductId) == 0) return;
        // Close the gap, so the next one added (Position = Count) never ties with one that is already there.
        for (int i = 0; i < _alternatives.Count; i++) _alternatives[i].MoveTo(i);
        MarkUpdated();
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

/// <summary>Join row: <c>AlternativeProductId</c> can be used instead of <c>ProductId</c>.</summary>
public sealed class ProductAlternative
{
    public Guid ProductId { get; private set; }
    public Guid AlternativeProductId { get; private set; }
    public int Position { get; private set; }

    private ProductAlternative() { }

    internal ProductAlternative(Guid productId, Guid alternativeProductId, int position)
    {
        ProductId = productId;
        AlternativeProductId = alternativeProductId;
        Position = position;
    }

    internal void MoveTo(int position) => Position = position;
}

public sealed class InvalidProductTransitionException : DomainException
{
    public InvalidProductTransitionException(ProductStatus from, ProductStatus to)
        : base("INVALID_PRODUCT_TRANSITION", $"Product cannot move from {from} to {to}.") { }
}
