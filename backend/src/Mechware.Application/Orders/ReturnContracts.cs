using Mechware.Application.Catalog;

namespace Mechware.Application.Orders;

/// <summary>
/// Return settings (section <c>Returns</c>, F2.1). DEFAULTS PENDING THE OWNER AND THE LAWYER (docs/LEGAL_REVIEW.md "returns"): the window
/// is 7 Dhaka calendar days after delivery (brief §8.10) and no category is non-returnable until the owner names some. A value out of
/// range stops the host at startup (<see cref="Validated"/>).
/// </summary>
public sealed class ReturnOptions
{
    public const string SectionName = "Returns";

    /// <summary>Days after the Dhaka delivery day a return can still be opened (1-60).</summary>
    public int WindowDays { get; set; } = 7;

    /// <summary>Comma-separated category slugs whose products (and those of every sub-category) cannot be returned.</summary>
    public string NonReturnableCategorySlugs { get; set; } = string.Empty;

    public IReadOnlySet<string> NonReturnableSlugs() =>
        NonReturnableCategorySlugs.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(s => s.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);

    public static ReturnOptions Validated(ReturnOptions? o)
    {
        o ??= new ReturnOptions();
        if (o.WindowDays is < 1 or > 60) throw new InvalidOperationException("Returns:WindowDays must be 1 to 60.");
        return o;
    }
}

/// <summary>
/// Open a return of the caller's own delivered order. <paramref name="CompanyId"/> must name the order's company for a company order
/// and be empty for a personal one (otherwise 404); the stored company is always the order's, never this value.
/// </summary>
public sealed record OpenReturnRequest(
    Guid UserId, string OrderNumber, string? IdempotencyKey, Guid? CompanyId, string? ReasonCode, string? Note, IReadOnlyList<ReturnLineInput>? Lines);

public sealed record ReturnLineInput(Guid OrderItemId, int Quantity);

public sealed record ReturnLineDto(Guid OrderItemId, Guid VariantId, string Sku, string Title, int Quantity);

/// <summary>A return request. <c>status</c>: requested, approved, rejected, received, refunded, credit_noted, withdrawn. <c>reasonCode</c>: damaged, wrong_item, not_as_described, other.</summary>
public sealed record ReturnRequestDto(
    Guid Id, Guid OrderId, string OrderNumber, Guid? CompanyId, Guid RequestedBy, string Status, string ReasonCode, string? Note,
    DateTime RequestedAtUtc, DateTime? WithdrawnAtUtc, IReadOnlyList<ReturnLineDto> Lines);

public sealed record OpenedReturnDto(ReturnRequestDto Return, bool Replayed);

/// <summary>Staff list filters: <c>Status</c> (wire name), <c>OrderNumber</c> (prefix), <c>CompanyId</c>.</summary>
public sealed record StaffReturnFilter(string? Status, string? OrderNumber, Guid? CompanyId, int Page, int PageSize);

public interface IReturnRequestService
{
    public const int MaxPageSize = 50;

    /// <summary>Opens a return; the same Idempotency-Key and body replays the first one (<c>Replayed</c>), another body is 409.</summary>
    Task<OpenedReturnDto> OpenAsync(OpenReturnRequest request, CancellationToken ct = default);
    Task<PagedResult<ReturnRequestDto>> ListOwnAsync(Guid userId, int page, int pageSize, CancellationToken ct = default);
    /// <summary>The caller's own return; anything else is the same 404.</summary>
    Task<ReturnRequestDto> GetOwnAsync(Guid userId, Guid returnId, CancellationToken ct = default);
    /// <summary>Requested → withdrawn; again for a withdrawn one answers it unchanged; a handled one is 409 <c>RETURN_NOT_WITHDRAWABLE</c>.</summary>
    Task<ReturnRequestDto> WithdrawAsync(Guid userId, Guid returnId, CancellationToken ct = default);
    Task<PagedResult<ReturnRequestDto>> ListStaffAsync(StaffReturnFilter filter, CancellationToken ct = default);
    Task<ReturnRequestDto> GetStaffAsync(Guid returnId, CancellationToken ct = default);
}
