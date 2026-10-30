namespace ShopVerse.Domain.Catalog;

public enum SlugResolutionKind { Current, Redirect, NotFound }

public sealed record SlugResolution(SlugResolutionKind Kind, Guid? EntityId, string? CurrentSlug);

/// <summary>
/// Pure slug → entity resolution with 301 semantics: a live slug wins; otherwise a retired slug
/// redirects to whatever the entity's slug is <i>now</i> (so A→B→C chains collapse to one hop).
/// </summary>
public static class SlugResolver
{
    public static SlugResolution Resolve(
        string requested,
        IReadOnlyDictionary<string, Guid> liveSlugs,
        IReadOnlyDictionary<Guid, string> currentSlugByEntity,
        IReadOnlyDictionary<string, Guid> retiredSlugs)
    {
        if (liveSlugs.TryGetValue(requested, out var liveId))
            return new SlugResolution(SlugResolutionKind.Current, liveId, requested);

        if (retiredSlugs.TryGetValue(requested, out var entityId) && currentSlugByEntity.TryGetValue(entityId, out var current))
            return new SlugResolution(SlugResolutionKind.Redirect, entityId, current);

        return new SlugResolution(SlugResolutionKind.NotFound, null, null);
    }
}
