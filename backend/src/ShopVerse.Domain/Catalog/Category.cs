using ShopVerse.Domain.Common;
using ShopVerse.Domain.Common.Exceptions;
using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Catalog;

/// <summary>
/// Hierarchical catalog node. <see cref="MaterializedPath"/> is a dot-separated chain of slugs
/// starting at <c>root</c> (e.g. <c>root.industrial-automation.plcs</c>) enabling prefix sub-tree queries.
/// </summary>
public sealed class Category : AggregateRoot<Guid>
{
    public const int MaxDepth = 2;

    public Guid? ParentId { get; private set; }
    public string NameEn { get; private set; } = string.Empty;
    public string NameBn { get; private set; } = string.Empty;
    public string Slug { get; private set; } = string.Empty;
    public string MaterializedPath { get; private set; } = string.Empty;
    public int Depth { get; private set; }
    public int SortOrder { get; private set; }
    public string? ImageUrl { get; private set; }
    public decimal CommissionRate { get; private set; } = 0.08m;
    public decimal VatRate { get; private set; } = 0.05m;

    /// <summary>JSON array describing dynamic filter attributes for the category (JSONB).</summary>
    public string AttributeSchemaJson { get; private set; } = "[]";

    public string? SeoTitle { get; private set; }
    public string? SeoDescription { get; private set; }

    private Category() { }

    public static Category CreateRoot(
        Guid id, string nameEn, string nameBn, string slug, int sortOrder = 0, string? imageUrl = null,
        decimal vatRate = 0.05m, decimal commissionRate = 0.08m, string attributeSchemaJson = "[]") =>
        Build(id, null, null, nameEn, nameBn, slug, sortOrder, imageUrl, vatRate, commissionRate, attributeSchemaJson);

    public static Category CreateChild(
        Category parent, Guid id, string nameEn, string nameBn, string slug, int sortOrder = 0, string? imageUrl = null,
        decimal? vatRate = null, decimal? commissionRate = null, string attributeSchemaJson = "[]")
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.Depth >= MaxDepth)
        {
            throw new ValidationException("parentId", $"Categories support at most {MaxDepth + 1} levels.");
        }

        return Build(id, parent.Id, parent, nameEn, nameBn, slug, sortOrder, imageUrl,
            vatRate ?? parent.VatRate, commissionRate ?? parent.CommissionRate, attributeSchemaJson);
    }

    private static Category Build(
        Guid id, Guid? parentId, Category? parent, string nameEn, string nameBn, string slug, int sortOrder,
        string? imageUrl, decimal vatRate, decimal commissionRate, string attributeSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(nameEn)) throw new ValidationException("nameEn", "English name is required.");
        if (string.IsNullOrWhiteSpace(nameBn)) throw new ValidationException("nameBn", "Bengali name is required.");
        if (vatRate is < 0m or > 0.5m) throw new ValidationException("vatRate", "VAT rate must be between 0 and 0.5.");
        if (commissionRate is < 0m or > 1m) throw new ValidationException("commissionRate", "Commission rate must be between 0 and 1.");

        var validSlug = Catalog.Slug.Require(slug);
        return new Category(id)
        {
            ParentId = parentId,
            NameEn = nameEn.Trim(),
            NameBn = nameBn.Trim(),
            Slug = validSlug,
            MaterializedPath = parent is null ? $"root.{validSlug}" : $"{parent.MaterializedPath}.{validSlug}",
            Depth = parent is null ? 0 : parent.Depth + 1,
            SortOrder = sortOrder,
            ImageUrl = SafeUrl.RequireOptional(imageUrl, "imageUrl"),
            VatRate = vatRate,
            CommissionRate = commissionRate,
            AttributeSchemaJson = string.IsNullOrWhiteSpace(attributeSchemaJson) ? "[]" : attributeSchemaJson
        };
    }

    private Category(Guid id) : base(id) { }

    public bool IsLeaf(bool hasChildren) => !hasChildren;

    public bool IsAncestorOf(Category other) =>
        other.MaterializedPath.StartsWith(MaterializedPath + ".", StringComparison.Ordinal);

    /// <summary>
    /// Renames the slug and returns the retired <see cref="SlugHistory"/> row (null when unchanged).
    /// Descendant paths must be rewritten by the caller (see <see cref="RebasePath"/>).
    /// </summary>
    public SlugHistory? ChangeSlug(string newSlug, Guid historyId)
    {
        var validSlug = Catalog.Slug.Require(newSlug);
        if (validSlug == Slug) return null;

        var old = new SlugHistory(historyId, SlugEntityType.Category, Id, Slug);
        var oldPath = MaterializedPath;
        Slug = validSlug;
        MaterializedPath = oldPath[..(oldPath.LastIndexOf('.') + 1)] + validSlug;
        Touch();
        return old;
    }

    /// <summary>Rewrites this node's path prefix after an ancestor slug change.</summary>
    public void RebasePath(string oldPrefix, string newPrefix)
    {
        if (MaterializedPath.StartsWith(oldPrefix + ".", StringComparison.Ordinal))
        {
            MaterializedPath = newPrefix + MaterializedPath[oldPrefix.Length..];
            Touch();
        }
    }

    public void UpdateDetails(string nameEn, string nameBn, string? imageUrl, string? seoTitle, string? seoDescription,
        decimal vatRate, decimal commissionRate, string attributeSchemaJson)
    {
        if (string.IsNullOrWhiteSpace(nameEn)) throw new ValidationException("nameEn", "English name is required.");
        if (string.IsNullOrWhiteSpace(nameBn)) throw new ValidationException("nameBn", "Bengali name is required.");
        if (vatRate is < 0m or > 0.5m) throw new ValidationException("vatRate", "VAT rate must be between 0 and 0.5.");

        NameEn = nameEn.Trim();
        NameBn = nameBn.Trim();
        ImageUrl = SafeUrl.RequireOptional(imageUrl, "imageUrl");
        SeoTitle = seoTitle;
        SeoDescription = seoDescription;
        VatRate = vatRate;
        CommissionRate = commissionRate;
        AttributeSchemaJson = string.IsNullOrWhiteSpace(attributeSchemaJson) ? "[]" : attributeSchemaJson;
        Touch();
    }

    private void Touch() => UpdatedAtUtc = DateTime.UtcNow;
}
