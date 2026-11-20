using Mechware.Domain.Quotes;

namespace Mechware.Application.Quotes;

/// <summary>
/// One quote as its company's member sees it (ADR-0011 §12, §13): the newest version the buyer may see (Sent or later; drafts and versions
/// waiting for approval never). <paramref name="Status"/> is <c>sent</c>, <c>expired</c> (sent, past its validity), <c>accepted</c>,
/// <c>rejected</c>, <c>withdrawn</c> or <c>superseded</c>. Built field by field: no cost, floor, flags, reference price, internal note,
/// approval data or staff ids.
/// </summary>
public sealed record BuyerQuoteSummaryDto(Guid Id, string Number, Guid? RfqId, int VersionNo, string Status, DateTime? SentAt, DateTime? ValidUntil,
    int LineCount, long GoodsTotalMinor, long? GrandTotalMinor, string PaymentTerms, string ShippingMode, bool ContactIsMe, DateTime? ClosedAt);

/// <summary>A quoted line: the frozen snapshot of the send (VAT-exclusive unit price, the rate and money of that moment).</summary>
public sealed record BuyerQuoteLineDto(Guid Id, int LineNo, Guid VariantId, string Sku, string? PartNumber, string TitleEn, string TitleBn, int Quantity,
    long UnitPriceMinor, decimal VatRate, long LineNetMinor, long LineTaxMinor, long LineTotalMinor, string? LineNote);

/// <summary>
/// A version the buyer may see, as it was sent. <paramref name="Acceptable"/>: it may be accepted now (§8.6; the accept endpoint decides).
/// <paramref name="ContentHash"/> goes back in the accept preview and the acceptance. A withdrawal shows when, not the staff reason.
/// </summary>
public sealed record BuyerQuoteVersionDto(int VersionNo, string Status, bool Acceptable, DateTime? SentAt, DateTime? ValidUntil, int ValidityDays,
    string PaymentTerms, string ShippingMode, Guid? ShipToSiteId, QuoteShipTo? ShipTo, IReadOnlyList<QuoteShippingFee>? ShippingFees, string? TermsText,
    string? BuyerNote, QuoteCompanySnapshot? Company, string? PreparerName, string? ContentHash, long GoodsNetMinor, long TaxMinor, long GoodsTotalMinor,
    long? ShippingTotalMinor, long? GrandTotalMinor, Guid? AcceptedOrderId, DateTime? AcceptedAt, DateTime? RejectedAt, string? RejectReason,
    DateTime? WithdrawnAt, DateTime? SupersededAt, IReadOnlyList<BuyerQuoteLineDto> Lines);

public sealed record BuyerQuoteDetailDto(BuyerQuoteSummaryDto Quote, IReadOnlyList<BuyerQuoteVersionDto> Versions);

public sealed record BuyerQuotePage(IReadOnlyList<BuyerQuoteSummaryDto> Items, int Page, int PageSize, int Total);

/// <summary>Where an e-mailed quote number lives, for the link (§12).</summary>
public sealed record BuyerQuoteRefDto(Guid CompanyId, Guid QuoteId, string Number);

/// <summary>
/// One line of the accept preview (§8.4): the order line acceptance would make now. <paramref name="VatRateChanged"/>: the category's rate is
/// not the one of the send; <paramref name="LowerPriceNow"/>: the buyer's own price today (contract, list) is below the quoted one, so ordering
/// normally may be cheaper; <paramref name="StockShort"/>: fewer free units than the quantity right now (acceptance would be refused).
/// </summary>
public sealed record QuoteAcceptPreviewLineDto(Guid QuoteLineId, Guid VariantId, string Sku, string Title, int Quantity, long UnitPriceMinor, decimal VatRate,
    long NetMinor, long TaxMinor, long TotalMinor, bool VatRateChanged, bool LowerPriceNow, bool StockShort);

/// <summary>What accepting would charge now, by the acceptance's own pricing path, with nothing held or written. Send <paramref name="GrandTotalMinor"/> as <c>expectedGrandTotalMinor</c>.</summary>
public sealed record QuoteAcceptPreviewDto(Guid QuoteId, int VersionNo, IReadOnlyList<QuoteAcceptPreviewLineDto> Lines, long SubtotalMinor, long TaxMinor,
    long ShippingMinor, long GrandTotalMinor, bool VatRateChanged, bool LowerPriceNow, bool StockShort);

/// <summary>
/// The buyer side of quotes (ADR-0011 §8.8, §12, §13; D4.10). Every refusal about access, whatever its reason (unknown or foreign company or
/// quote, not a member, a role that may not see it, a Pending or Rejected company, a malformed number), is <c>KeyNotFoundException</c> with
/// one message ("Quote not found."), so nothing tells the cases apart.
/// </summary>
public interface IBuyerQuoteService
{
    Task<BuyerQuotePage> ListAsync(Guid actorUserId, Guid companyId, string? status, int page, int pageSize, CancellationToken ct = default);
    Task<BuyerQuoteDetailDto> GetAsync(Guid actorUserId, Guid companyId, Guid quoteId, CancellationToken ct = default);
    Task<BuyerQuoteRefDto> FindByNumberAsync(Guid actorUserId, string? number, CancellationToken ct = default);

    /// <summary>The contact, a Buyer or an Admin says no to a sent version, with an optional "what should change" (§8.8); 403 <c>COMPANY_ROLE_REQUIRED</c> for anyone else who sees it.</summary>
    Task<BuyerQuoteDetailDto> RejectAsync(Guid actorUserId, Guid companyId, Guid quoteId, int? versionNo, string? reason, CancellationToken ct = default);
}
