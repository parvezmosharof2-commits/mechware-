using ShopVerse.Domain.Common.Primitives;

namespace ShopVerse.Domain.Catalog;

/// <summary>Raised whenever a product's searchable or displayed state changes; relayed to the search sync queue.</summary>
public sealed record ProductUpdatedEvent(Guid ProductId, string Slug, ProductStatus Status) : DomainEventBase;

/// <summary>Raised when a product slug changes so caches keyed by the old slug can be dropped.</summary>
public sealed record ProductSlugChangedEvent(Guid ProductId, string OldSlug, string NewSlug) : DomainEventBase;
