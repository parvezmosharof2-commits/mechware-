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

/// <summary>A returned line as the buyer sees it: <c>AcceptedQuantity</c> is null until inspected.</summary>
public sealed record ReturnLineDto(Guid OrderItemId, Guid VariantId, string Sku, string Title, int Quantity, int? AcceptedQuantity);

/// <summary>
/// A return request as the buyer sees it (no staff ids, no inspection notes, no restock figures). <c>status</c>: requested, approved,
/// rejected, received, inspected (awaiting the refund or credit note), refunded, credit_noted, withdrawn. <c>reasonCode</c>: damaged,
/// wrong_item, not_as_described, other. <c>RejectionReason</c> is the staff's reason for a rejected return.
/// </summary>
public sealed record ReturnRequestDto(
    Guid Id, Guid OrderId, string OrderNumber, Guid? CompanyId, Guid RequestedBy, string Status, string ReasonCode, string? Note,
    DateTime RequestedAtUtc, DateTime? WithdrawnAtUtc, DateTime? DecidedAtUtc, string? RejectionReason, DateTime? ReceivedAtUtc,
    DateTime? InspectedAtUtc, IReadOnlyList<ReturnLineDto> Lines);

/// <summary>A returned line for staff: its id (to inspect it), what was accepted and restocked, and the internal note.</summary>
public sealed record StaffReturnLineDto(
    Guid Id, Guid OrderItemId, Guid VariantId, string Sku, string Title, int Quantity, int? AcceptedQuantity, int? RestockQuantity, string? InspectionNote);

/// <summary>
/// A return for staff. <c>RowVersion</c> goes back with every decision (stale: 409 <c>RETURN_CHANGED</c>). <c>RestockPending</c>: inspected
/// with units to restock that are not yet back on the shelf (the post-commit step failed or has not run; the E7.3 sweep finishes it).
/// </summary>
public sealed record StaffReturnDto(
    Guid Id, Guid OrderId, string OrderNumber, Guid? CompanyId, Guid RequestedBy, string Status, string ReasonCode, string? Note,
    DateTime RequestedAtUtc, DateTime? WithdrawnAtUtc, Guid? DecidedBy, DateTime? DecidedAtUtc, string? RejectionReason,
    Guid? ReceivedBy, DateTime? ReceivedAtUtc, Guid? InspectedBy, DateTime? InspectedAtUtc, bool RestockPending, uint RowVersion,
    IReadOnlyList<StaffReturnLineDto> Lines);

/// <summary>A staff move without other input: the return's <c>rowVersion</c> from the last read (required).</summary>
public sealed record ReturnStepRequest(uint? RowVersion);

/// <summary>Reject with a reason (5-500 plain text; the buyer sees it).</summary>
public sealed record RejectReturnRequest(uint? RowVersion, string? Reason);

/// <summary>One line of an inspection: accepted 0..asked, restocked 0..accepted (the rest is scrapped), an internal note (≤ 500).</summary>
public sealed record ReturnInspectionInput(Guid ReturnLineId, int AcceptedQty, int RestockQty, string? Note);

/// <summary>Every line of the return, each once.</summary>
public sealed record InspectReturnRequest(uint? RowVersion, IReadOnlyList<ReturnInspectionInput>? Lines);

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
    Task<PagedResult<StaffReturnDto>> ListStaffAsync(StaffReturnFilter filter, CancellationToken ct = default);
    Task<StaffReturnDto> GetStaffAsync(Guid returnId, CancellationToken ct = default);

    // F2.3a staff moves. Each checks the rowVersion (409 RETURN_CHANGED) and the status (409 RETURN_INVALID_TRANSITION), locks the order row
    // then the return, writes an audit row and an outbox event. No money moves here (refund / credit note: F2.3b after E6.5).

    /// <summary>Requested → approved.</summary>
    Task<StaffReturnDto> ApproveAsync(Guid returnId, ReturnStepRequest request, CancellationToken ct = default);
    /// <summary>Requested → rejected (reason 5-500); its quantities are free for a new return.</summary>
    Task<StaffReturnDto> RejectAsync(Guid returnId, RejectReturnRequest request, CancellationToken ct = default);
    /// <summary>Approved → received (the goods arrived).</summary>
    Task<StaffReturnDto> MarkReceivedAsync(Guid returnId, ReturnStepRequest request, CancellationToken ct = default);
    /// <summary>Received → inspected; after the commit the restocked units go back on the shelf against the order's deduction.</summary>
    Task<StaffReturnDto> InspectAsync(Guid returnId, InspectReturnRequest request, CancellationToken ct = default);
}
