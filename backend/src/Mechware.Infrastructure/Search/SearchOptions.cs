using System.ComponentModel.DataAnnotations;

namespace Mechware.Infrastructure.Search;

/// <summary>Meilisearch connection. Section <c>Search</c>; environment keys <c>Search__Url</c>, <c>Search__ApiKey</c>, <c>Search__IndexName</c>.</summary>
public sealed class SearchOptions
{
    public const string SectionName = "Search";

    [Required, Url]
    public string Url { get; set; } = "http://localhost:7700";

    /// <summary>Master or search/admin key. Never logged. Outside Development it must be set (see <c>AddSearch</c>).</summary>
    public string ApiKey { get; set; } = "";

    [Required, RegularExpression("^[A-Za-z0-9_-]{1,64}$")]
    public string IndexName { get; set; } = "products";

    [Range(1, 120)]
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>How long to wait for the engine to finish an indexing task before giving up.</summary>
    [Range(1, 600)]
    public int TaskTimeoutSeconds { get; set; } = 60;
}
