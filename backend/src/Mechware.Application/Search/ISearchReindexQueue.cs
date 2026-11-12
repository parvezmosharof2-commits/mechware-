namespace Mechware.Application.Search;

/// <summary>
/// Asks the search indexer to reload products whose indexed state changed without a product event (stock, category slug).
/// Best effort by contract: an implementation must never throw, because the business change has already been committed;
/// the nightly or manual rebuild repairs any drift.
/// </summary>
public interface ISearchReindexQueue
{
    Task EnqueueProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default);
}
