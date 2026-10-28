# ADR 0005: Search Provider Abstraction and Meilisearch Integration

## Status
Accepted

## Date
2026-10-29

## Context
Industrial purchasers search using precise technical specifications (e.g., "PLC 24V DC 14DI 10DO transistor", "0.75kW 3-phase VFD", "M12 inductive proximity sensor PNP NO"), part numbers, manufacturer brands (Siemens, Delta, Mitsubishi, Omron), or dual languages (English and Bangla: "ইনভার্টার", "পিএলসি").
Requirements:
- Autocomplete response time < 50ms (p95).
- Typo tolerance (e.g. "Seimens" -> "Siemens", "Omrom" -> "Omron").
- Multi-attribute faceting (voltage, phase, power rating, I/O count, mounting type, communication protocols).
- Synonyms and Banglish transliteration ("mobile = phone = মোবাইল", "vfd = inverter = ড্রাইভ").
- Decoupling core business logic from search engine vendor lock-in.

## Decision
We implement a **Port and Adapter pattern** for search discovery:

```
[ ShopVerse.Application ]
           │
           ▼
[ Port: ISearchProvider ]
           │
   ┌───────┴───────────────────────┐
   ▼                               ▼
[ MeilisearchAdapter ]    [ MockSearchProvider ]
(Production / Dev)        (Unit Tests / CI)
```

### 1. Port Interface (`ShopVerse.Application/Common/Interfaces/ISearchProvider.cs`)
```csharp
public interface ISearchProvider
{
    Task<SearchResultDto<ProductSearchDocument>> SearchAsync(
        SearchQuery query, 
        CancellationToken ct = default);

    Task<AutocompleteResultDto> AutocompleteAsync(
        string prefix, 
        int limit = 5, 
        CancellationToken ct = default);

    Task IndexProductsAsync(
        IEnumerable<ProductSearchDocument> documents, 
        CancellationToken ct = default);

    Task DeleteProductsAsync(
        IEnumerable<Guid> productIds, 
        CancellationToken ct = default);

    Task UpdateSynonymsAsync(
        Dictionary<string, List<string>> synonyms, 
        CancellationToken ct = default);
}
```

### 2. Search Engine Choice: Meilisearch
- **Why Meilisearch**:
  - Sub-50ms search and prefix search latency out of the box.
  - Native typo-tolerance, custom stop-words, and synonym support.
  - Excellent dynamic faceting for arbitrary JSONB product specs without complex schema migrations.
  - Lightweight self-hosted Docker footprint compared to Elasticsearch/OpenSearch.
  - Official .NET client (`Meilisearch-dotnet`).

### 3. Asynchronous Data Synchronization via Transactional Outbox
1. Product mutations (create, update, price change, stock status change, delete) emit a `ProductUpdatedDomainEvent` or `ProductStockStatusChangedDomainEvent`.
2. The domain event is saved to the PostgreSQL `outbox_messages` table within the same ACID transaction as the product record.
3. The background outbox processor (`ShopVerse.Worker`) reads pending messages, maps them to `ProductSearchDocument`, and publishes them in batches to Meilisearch with retry policies (Polly).
4. Direct synchronous indexing from API controllers is prohibited to guarantee zero latency penalty on write operations.

## Consequences
### Positive:
- Blazing-fast (<50ms) storefront search and autocomplete.
- Eventual consistency guarantee via outbox relay prevents search index desynchronization.
- Clean architectural boundary allows swapping to OpenSearch or Algolia by implementing `ISearchProvider` without altering domain logic.

### Negative:
- Eventual consistency introduces a negligible delay (~200ms) between product updates in the database and appearance in search results.
