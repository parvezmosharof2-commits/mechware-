using System.Text.Json.Serialization;

namespace Mechware.Application.Pricing;

// Create and edit requests have no status, version, approver, submitter, author or series id (ADR-0008 §3, PL4): the server
// binds those from nothing, so a body that carries them is simply ignored.

/// <param name="Scope">"General" or "Company" (group lists come with D2.14).</param>
public sealed record CreatePriceListRequest(string? Scope, Guid? CompanyId, string? ContractRef, string? Name, DateTime ValidFromUtc, DateTime ValidToUtc);

public sealed record UpdatePriceListHeaderRequest(string? Name, string? ContractRef, DateTime ValidFromUtc, DateTime ValidToUtc);

/// <summary>Upsert by (variant, minQty): an existing entry gets the new price and reason, a new one is added.</summary>
public sealed record UpsertPriceListEntriesRequest(IReadOnlyList<UpsertPriceListEntryItem>? Entries);

public sealed record UpsertPriceListEntryItem(Guid VariantId, int MinQty, long UnitPriceMinor, string? BelowFloorReason);

public sealed record PriceListQuery(string? Status, string? Scope, Guid? CompanyId, string? Q, int Page, int PageSize);

public record PriceListSummaryDto
{
    public Guid Id { get; init; }
    public Guid SeriesId { get; init; }
    public int Version { get; init; }
    public string Name { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public Guid? CompanyId { get; init; }
    public string? ContractRef { get; init; }

    /// <summary>The stored status: Draft, PendingApproval, Approved or Withdrawn.</summary>
    public string Status { get; init; } = string.Empty;

    /// <summary>Draft, PendingApproval, Withdrawn, or for an approved list Scheduled, Active, Expired or Superseded (from the clock and the series).</summary>
    public string DisplayState { get; init; } = string.Empty;

    public DateTime ValidFromUtc { get; init; }
    public DateTime ValidToUtc { get; init; }
    public int EntryCount { get; init; }
    public Guid CreatedBy { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public Guid? SubmittedBy { get; init; }
    public DateTime? SubmittedAtUtc { get; init; }
}

public sealed record PriceListPageDto(IReadOnlyList<PriceListSummaryDto> Items, int Page, int PageSize, int Total);

/// <summary>
/// The cost, floor and flag members are present only for a caller holding <c>pricing.view_cost</c>; for everyone else they are
/// null and left out of the JSON entirely (PL12).
/// </summary>
public sealed record PriceListEntryDto
{
    public Guid Id { get; init; }
    public Guid VariantId { get; init; }
    public string Sku { get; init; } = string.Empty;
    public string Title { get; init; } = string.Empty;
    public int MinQty { get; init; }
    public long UnitPriceMinor { get; init; }
    public string? BelowFloorReason { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? CostMinor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public long? FloorMinor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BelowFloor { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? BelowCost { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public bool? CostUnknown { get; init; }
}

public sealed record PriceListDetailDto : PriceListSummaryDto
{
    /// <summary>Set at submit; the approver must send it back (D2.8).</summary>
    public string? ContentHash { get; init; }

    public DateTime? EffectiveFromUtc { get; init; }
    public Guid? ApprovedBy { get; init; }
    public DateTime? ApprovedAtUtc { get; init; }
    public string? RejectionReason { get; init; }
    public string? WithdrawReason { get; init; }
    public IReadOnlyList<Guid> Contributors { get; init; } = [];
    public IReadOnlyList<PriceListEntryDto> Entries { get; init; } = [];
}

/// <summary>Staff price-list administration, part 1 (D2.7): everything except approve, reject and withdraw (D2.8).</summary>
public interface IPriceListService
{
    Task<PriceListPageDto> ListAsync(PriceListQuery query, CancellationToken ct = default);

    Task<PriceListDetailDto> GetAsync(Guid id, CancellationToken ct = default);

    Task<PriceListDetailDto> CreateAsync(CreatePriceListRequest request, CancellationToken ct = default);

    Task<PriceListDetailDto> UpdateHeaderAsync(Guid id, UpdatePriceListHeaderRequest request, CancellationToken ct = default);

    Task<PriceListDetailDto> UpsertEntriesAsync(Guid id, UpsertPriceListEntriesRequest request, CancellationToken ct = default);

    Task<PriceListDetailDto> RemoveEntryAsync(Guid id, Guid entryId, CancellationToken ct = default);

    Task<PriceListDetailDto> SubmitAsync(Guid id, CancellationToken ct = default);

    Task<PriceListDetailDto> ReviseAsync(Guid id, CancellationToken ct = default);

    /// <summary>Deletes a Draft only (409 otherwise).</summary>
    Task DeleteDraftAsync(Guid id, CancellationToken ct = default);
}
