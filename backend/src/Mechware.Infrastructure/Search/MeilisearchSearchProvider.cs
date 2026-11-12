using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Mechware.Application.Search;
using Mechware.Domain.Common.Exceptions;
using Microsoft.Extensions.Logging;

namespace Mechware.Infrastructure.Search;

/// <summary>
/// Meilisearch adapter for <see cref="ISearchProvider"/> (ADR-0005), written against the engine's HTTP API directly so the
/// index settings stay fully under our control and no extra package is needed. Any engine failure becomes a 503
/// (<see cref="ServiceUnavailableException"/>); the engine's own message is logged, never returned to the caller.
/// </summary>
public sealed class MeilisearchSearchProvider : ISearchProvider
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Settings applied by <see cref="EnsureIndexAsync"/>. Raw part numbers rank first in free text. Exact part-number lookup
    /// does not depend on the tokenizer (which splits "6ES72141AG400XB0" into pieces): it filters on <c>partNumberKeys</c>.
    /// Disabling typos on <c>partNumbers</c> was tried and breaks even exact matches, because re-joining the pieces counts as a typo.
    /// </summary>
    internal static readonly JsonObject IndexSettings = new()
    {
        ["searchableAttributes"] = new JsonArray("partNumbers", "titleEn", "titleBn", "brandName"),
        ["filterableAttributes"] = new JsonArray("partNumberKeys", "brandSlug", "categoryPath", "inStock", "supplyStatus", "priceMinor"),
        ["sortableAttributes"] = new JsonArray("priceMinor", "soldCount", "ratingAvg"),
        ["rankingRules"] = new JsonArray("words", "typo", "proximity", "attribute", "sort", "exactness", "soldCount:desc"),
        ["typoTolerance"] = new JsonObject
        {
            ["enabled"] = true,
            ["minWordSizeForTypos"] = new JsonObject { ["oneTypo"] = 4, ["twoTypos"] = 8 },
        },
        ["pagination"] = new JsonObject { ["maxTotalHits"] = SearchQuery.MaxPage * SearchQuery.MaxPageSize },
    };

    private static readonly string[] FacetFields = ["brandSlug", "categoryPath", "supplyStatus", "inStock"];

    private readonly HttpClient _http;
    private readonly string _index;
    private readonly TimeSpan _taskTimeout;
    private readonly ILogger<MeilisearchSearchProvider> _logger;

    public MeilisearchSearchProvider(HttpClient http, SearchOptions options, ILogger<MeilisearchSearchProvider> logger)
    {
        _http = http;
        _index = options.IndexName;
        _taskTimeout = TimeSpan.FromSeconds(options.TaskTimeoutSeconds);
        _logger = logger;
        _http.BaseAddress ??= new Uri(options.Url.TrimEnd('/') + "/");
        _http.Timeout = TimeSpan.FromSeconds(options.TimeoutSeconds);
        if (!string.IsNullOrEmpty(options.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiKey);
    }

    public Task EnsureIndexAsync(CancellationToken ct = default) => EnsureAsync(_index, ct);

    private async Task EnsureAsync(string index, CancellationToken ct)
    {
        var exists = await SendAsync(HttpMethod.Get, $"indexes/{index}", null, ct, allow404: true);
        if (exists is null)
            await WaitAsync(await SendAsync(HttpMethod.Post, "indexes", new JsonObject { ["uid"] = index, ["primaryKey"] = "id" }, ct), ct);
        await WaitAsync(await SendAsync(HttpMethod.Patch, $"indexes/{index}/settings", IndexSettings.DeepClone(), ct), ct);
    }

    public async Task ReplaceAllAsync(IAsyncEnumerable<IReadOnlyCollection<ProductSearchDocument>> batches, CancellationToken ct = default)
    {
        await EnsureAsync(_index, ct);
        var fresh = $"{_index}_rebuild_{Guid.NewGuid():N}";
        try
        {
            await EnsureAsync(fresh, ct);
            if (await SendAsync(HttpMethod.Get, $"indexes/{_index}/settings/synonyms", null, ct) is JsonObject { Count: > 0 } synonyms)
                await WaitAsync(await SendAsync(HttpMethod.Put, $"indexes/{fresh}/settings/synonyms", synonyms.DeepClone(), ct), ct);
            await foreach (var batch in batches.WithCancellation(ct))
                await IndexIntoAsync(fresh, batch, ct);
            var swap = new JsonArray(new JsonObject { ["indexes"] = new JsonArray(_index, fresh) });
            await WaitAsync(await SendAsync(HttpMethod.Post, "swap-indexes", swap, ct), ct);
        }
        finally
        {
            // After the swap `fresh` holds the old documents; before it, a half-built index. Either way it goes.
            try { await SendAsync(HttpMethod.Delete, $"indexes/{fresh}", null, CancellationToken.None, allow404: true); }
            catch (ServiceUnavailableException) { _logger.LogWarning("Could not delete temporary index {Index}", fresh); }
        }
    }

    public async Task<SearchResultDto<ProductSearchDocument>> SearchAsync(SearchQuery query, CancellationToken ct = default)
    {
        var filter = SearchFilterBuilder.Build(query);
        var body = new JsonObject
        {
            ["q"] = query.Text,
            ["page"] = query.Page,
            ["hitsPerPage"] = query.PageSize,
            ["facets"] = new JsonArray(FacetFields.Select(f => (JsonNode)f).ToArray()),
        };
        if (filter is not null) body["filter"] = filter;
        if (SearchFilterBuilder.BuildSort(query.Sort) is { } sort) body["sort"] = new JsonArray(sort.Select(s => (JsonNode)s).ToArray());

        var response = await SendAsync(HttpMethod.Post, $"indexes/{_index}/search", body, ct)
                       ?? throw Unavailable("empty search response");
        var hits = response["hits"]?.Deserialize<List<ProductSearchDocument>>(Json) ?? [];
        var facets = new Dictionary<string, IReadOnlyDictionary<string, long>>();
        if (response["facetDistribution"] is JsonObject distribution)
            foreach (var (field, values) in distribution)
                facets[field] = (values as JsonObject)?.ToDictionary(kv => kv.Key, kv => kv.Value?.GetValue<long>() ?? 0)
                                ?? new Dictionary<string, long>();

        return new SearchResultDto<ProductSearchDocument>(
            hits,
            response["totalHits"]?.GetValue<long>() ?? hits.Count,
            query.Page,
            query.PageSize,
            response["processingTimeMs"]?.GetValue<int>() ?? 0,
            facets);
    }

    public async Task<AutocompleteResultDto> AutocompleteAsync(string prefix, int limit = 5, CancellationToken ct = default)
    {
        if (prefix.Length > SearchQuery.MaxTextLength)
            throw new ValidationException("prefix", $"At most {SearchQuery.MaxTextLength} characters.");
        var body = new JsonObject
        {
            ["q"] = prefix,
            ["limit"] = Math.Clamp(limit, 1, 10),
            ["attributesToRetrieve"] = new JsonArray("id", "slug", "titleEn", "titleBn"),
        };
        var response = await SendAsync(HttpMethod.Post, $"indexes/{_index}/search", body, ct)
                       ?? throw Unavailable("empty autocomplete response");
        var suggestions = (response["hits"] as JsonArray ?? [])
            .Select(h => new AutocompleteSuggestion(
                Guid.Parse(h!["id"]!.GetValue<string>()),
                h["slug"]?.GetValue<string>() ?? "",
                h["titleEn"]?.GetValue<string>() ?? "",
                h["titleBn"]?.GetValue<string>() ?? ""))
            .ToList();
        return new AutocompleteResultDto(suggestions, response["processingTimeMs"]?.GetValue<int>() ?? 0);
    }

    public Task IndexProductsAsync(IReadOnlyCollection<ProductSearchDocument> documents, CancellationToken ct = default) =>
        IndexIntoAsync(_index, documents, ct);

    private async Task IndexIntoAsync(string index, IReadOnlyCollection<ProductSearchDocument> documents, CancellationToken ct)
    {
        if (documents.Count == 0) return;
        var body = JsonSerializer.SerializeToNode(documents, Json);
        await WaitAsync(await SendAsync(HttpMethod.Post, $"indexes/{index}/documents", body, ct), ct);
    }

    public async Task DeleteProductsAsync(IReadOnlyCollection<Guid> productIds, CancellationToken ct = default)
    {
        if (productIds.Count == 0) return;
        var body = new JsonArray(productIds.Select(id => (JsonNode)id.ToString()).ToArray());
        await WaitAsync(await SendAsync(HttpMethod.Post, $"indexes/{_index}/documents/delete-batch", body, ct), ct);
    }

    public async Task UpdateSynonymsAsync(IReadOnlyDictionary<string, IReadOnlyList<string>> synonyms, CancellationToken ct = default)
    {
        var body = new JsonObject();
        foreach (var (word, same) in synonyms)
            body[word] = new JsonArray(same.Select(s => (JsonNode)s).ToArray());
        await WaitAsync(await SendAsync(HttpMethod.Put, $"indexes/{_index}/settings/synonyms", body, ct), ct);
    }

    /// <summary>Sends one request. Returns the parsed body, or null for 204 and (when allowed) 404.</summary>
    private async Task<JsonNode?> SendAsync(HttpMethod method, string path, JsonNode? body, CancellationToken ct, bool allow404 = false)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body is not null) request.Content = JsonContent.Create(body, options: Json);
            using var response = await _http.SendAsync(request, ct);
            if (allow404 && response.StatusCode == HttpStatusCode.NotFound) return null;
            if (!response.IsSuccessStatusCode)
            {
                var detail = await response.Content.ReadAsStringAsync(ct);
                _logger.LogError("Search engine answered {Status} to {Method} {Path}: {Detail}", (int)response.StatusCode, method, path, detail);
                throw Unavailable($"engine answered {(int)response.StatusCode}");
            }
            if (response.StatusCode == HttpStatusCode.NoContent) return null;
            return await response.Content.ReadFromJsonAsync<JsonNode>(Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            _logger.LogError(ex, "Search engine unreachable for {Method} {Path}", method, path);
            throw Unavailable("engine unreachable");
        }
    }

    /// <summary>Waits until the engine has finished the task named in <paramref name="accepted"/>; a failed task is an error.</summary>
    private async Task WaitAsync(JsonNode? accepted, CancellationToken ct)
    {
        var uid = accepted?["taskUid"]?.GetValue<long>() ?? throw Unavailable("no task id in the engine's answer");
        var clock = Stopwatch.StartNew();
        var delay = TimeSpan.FromMilliseconds(20);
        while (clock.Elapsed < _taskTimeout)
        {
            var task = await SendAsync(HttpMethod.Get, $"tasks/{uid}", null, ct) ?? throw Unavailable("task vanished");
            switch (task["status"]?.GetValue<string>())
            {
                case "succeeded": return;
                case "failed" or "canceled":
                    _logger.LogError("Search task {Uid} ended as {Status}: {Error}", uid, task["status"], task["error"]?["message"]);
                    throw Unavailable($"task {uid} did not succeed");
            }
            await Task.Delay(delay, ct);
            delay = TimeSpan.FromMilliseconds(Math.Min(delay.TotalMilliseconds * 2, 500));
        }
        throw Unavailable($"task {uid} timed out");
    }

    /// <summary>
    /// The caller sees one fixed message: details such as "engine answered 401" would tell an outsider how the engine is set up.
    /// The reason goes to the log and to <c>Data["reason"]</c> for tests and diagnostics.
    /// </summary>
    private ServiceUnavailableException Unavailable(string why)
    {
        _logger.LogWarning("Search unavailable: {Reason}", why);
        var ex = new ServiceUnavailableException("SEARCH_UNAVAILABLE", "Search is temporarily unavailable. Please try again shortly.");
        ex.Data["reason"] = why;
        return ex;
    }
}
