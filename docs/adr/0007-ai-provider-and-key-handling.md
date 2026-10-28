# ADR 0007: AI Provider Abstraction and Gemini API Key Security

## Status
Accepted

## Date
2026-10-29

## Context
MECHWARE integrates Generative AI (Google Gemini) for several high-value capabilities:
1. Natural language / semantic search parser on the storefront.
2. Automated review summarization and pros/cons extraction on Product Detail Pages (PDP).
3. Grounded Shopping Assistant (answering technical product inquiries using verified catalog data only).
4. Seller listing assistant (generating industrial titles, technical bullet points, and SEO metadata from raw specs or photos).
5. Automated moderation for reviews and Q&A.
6. Plain-language sales and anomaly summaries for admin dashboards.

Risks include: API key leakage, model hallucination (e.g. inventing non-existent stock or prices), unpredictable latency, and API rate limits / costs.

## Decision
We enforce a secure, server-side-only integration pattern:

```
[ Client Request (Chat / Search / Listing) ]
                 │
                 ▼
[ ShopVerse.Api (ASP.NET Core Web API) ]
                 │
                 ▼
[ ShopVerse.Application: IAiProvider ]
                 │
   ┌─────────────┴────────────────┐
   ▼                              ▼
[ GeminiAiProvider ]     [ Fallback / MockProvider ]
(Production / Dev)       (Offline / Rate-limited / Tests)
```

### 1. Key Security & Environment Isolation
- The Google Gemini API key is stored **exclusively** in the backend server configuration (`GEMINI_API_KEY` or `AiSettings:GeminiApiKey`).
- It is **never** bundled in frontend code, git commits, Docker images, HTTP response payloads, or error logs.
- Direct client-to-Gemini communication is strictly forbidden; all traffic flows through the backend API.

### 2. Provider Abstraction (`ShopVerse.Application/Common/Interfaces/IAiProvider.cs`)
```csharp
public interface IAiProvider
{
    Task<AiSearchFilterDto> ParseSearchQueryAsync(
        string naturalLanguageQuery, 
        CancellationToken ct = default);

    Task<AiReviewSummaryDto> SummarizeReviewsAsync(
        Guid productId, 
        IEnumerable<ReviewTextDto> reviews, 
        CancellationToken ct = default);

    Task<IAsyncEnumerable<string>> StreamChatAssistantAsync(
        AiShoppingChatRequest request, 
        CancellationToken ct = default);

    Task<AiListingDraftDto> GenerateListingDraftAsync(
        AiListingPromptDto input, 
        CancellationToken ct = default);

    Task<AiModerationResultDto> ModerateContentAsync(
        string content, 
        CancellationToken ct = default);
}
```

### 3. Anti-Hallucination & Tool Calling Grounding
- The Shopping Assistant operates with **Strict Tool Calling (Grounding)**:
  - System prompts instruct the model that it is an industrial sales engineer at MECHWARE.
  - It is explicitly prohibited from quoting prices, stock levels, or specifications unless retrieved through server-side tool calls (`SearchProducts`, `GetProductDetails`, `CheckAvailability`).
  - Responses clearly display the badge `"AI-generated summary"` or `"AI Assistant"`.

### 4. Resiliency, Caching, and Fallbacks
- **Polly Resilience**: All Gemini HTTP calls are wrapped with retry policies (exponential backoff with jitter) and circuit breakers.
- **Redis Response Caching**: Review summaries are computed and cached in Redis with tags (`cache:ai:review:{productId}`) and only regenerated when new approved reviews are published.
- **Graceful Fallback**: If the Gemini API times out (> 4s) or encounters quota exhaustion (HTTP 429), the API degrades gracefully:
  - Natural language search falls back to Meilisearch keyword search.
  - Product review summaries fall back to displaying standard customer star ratings and recent reviews.

## Consequences
### Positive:
- Zero risk of Gemini API key compromise.
- Deterministic and grounded responses eliminate price hallucinations.
- Caching minimizes external API token costs.
- Complete provider swappability (e.g. Gemini 1.5 Flash -> Gemini 2.0 Pro) without touching storefront UI code.

### Negative:
- Streaming AI responses require Server-Sent Events (SSE) or WebSockets from the C# backend to the Next.js frontend.
