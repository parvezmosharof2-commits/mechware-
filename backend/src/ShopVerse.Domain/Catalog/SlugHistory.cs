using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Catalog;

public enum SlugEntityType
{
    Product = 1,
    Category = 2
}

/// <summary>
/// A retired slug that must 301-redirect to the entity's current slug.
/// <see cref="OldSlug"/> is globally unique per entity type.
/// </summary>
public sealed class SlugHistory : Entity<Guid>
{
    public SlugEntityType EntityType { get; private set; }
    public Guid EntityId { get; private set; }
    public string OldSlug { get; private set; } = string.Empty;

    private SlugHistory() { }

    public SlugHistory(Guid id, SlugEntityType entityType, Guid entityId, string oldSlug) : base(id)
    {
        EntityType = entityType;
        EntityId = entityId;
        OldSlug = Slug.Require(oldSlug);
    }
}
