using Mechware.Application.Catalog;
using Mechware.Domain.Orders;

namespace Mechware.Application.Orders;

/// <summary>A catalogue variant as the storefront shows it (public data only).</summary>
public sealed record QuickOrderVariant(
    Guid Id, string Sku, string? PartNumber, string Title, string TitleBn, string? Brand, string Slug,
    IReadOnlyDictionary<string, string> Options);

/// <summary>The part that replaces an obsolete one. Offered, never swapped in: the buyer decides.</summary>
public sealed record QuickOrderSuccessor(QuickOrderVariant Variant, bool IsDropIn, string? Note);

/// <param name="Kind">camelCase: <c>belowMinimumOrder</c>, <c>quantityAdjustedToPack</c> (the cart's notice kinds), <c>nrnd</c>, <c>sameVariantOnSeveralLines</c>.</param>
public sealed record QuickOrderLineNotice(string Kind, int? Was, int? Now);

/// <summary>
/// One validated line. <see cref="Status"/> is a stable machine code: <c>OK</c>, <c>PART_NOT_FOUND</c> (also an unpublished,
/// draft, archived or inactive item), <c>PART_AMBIGUOUS</c>, <c>PART_OBSOLETE</c>, <c>OUT_OF_STOCK</c>, <c>INSUFFICIENT_STOCK</c>,
/// <c>QTY_EXCEEDS_LIMIT</c>. Only <c>OK</c> lines are <see cref="Valid"/>. Prices are public, VAT-inclusive, for the adjusted
/// <see cref="Quantity"/>, from the same resolver as an order.
/// </summary>
public sealed record QuickOrderLineResult(
    IReadOnlyList<int> LineNumbers, string Raw, string Part, int RequestedQuantity,
    string Status, string Message, bool Valid,
    QuickOrderVariant? Variant, int? Quantity, IReadOnlyList<QuickOrderLineNotice> Notices,
    StockDto? Stock, long? UnitPriceMinor, long? LineTotalMinor, string? PriceSource,
    QuickOrderSuccessor? Successor, IReadOnlyList<QuickOrderVariant> Candidates, IReadOnlyList<QuickOrderVariant> Suggestions);

/// <param name="SubtotalMinor">Sum of the valid lines' totals only, in poisha (BDT).</param>
public sealed record QuickOrderSummary(int Lines, int Valid, int NeedsAttention, long SubtotalMinor, string Currency);

public sealed record QuickOrderValidation(
    IReadOnlyList<QuickOrderLineResult> Lines, IReadOnlyList<QuickOrderIssue> Errors, IReadOnlyList<QuickOrderIssue> Notices,
    QuickOrderSummary Summary);

/// <summary>
/// Validates pasted quick-order text against the public catalogue (D5.2). Reads only: no company, no user, no cart.
/// Throws <c>UnprocessableException</c> TOO_MANY_LINES / INPUT_INVALID and <c>PayloadTooLargeException</c> INPUT_TOO_LARGE
/// when the parser refuses the whole input.
/// </summary>
public interface IQuickOrderValidator
{
    Task<QuickOrderValidation> ValidateAsync(string? text, CancellationToken ct = default);
}
