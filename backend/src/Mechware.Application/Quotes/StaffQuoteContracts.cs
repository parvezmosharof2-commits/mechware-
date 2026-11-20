using System.Text.Json.Serialization;
using Mechware.Domain.Quotes;

namespace Mechware.Application.Quotes;

/// <summary>Filters of the staff lists (ADR-0011 §12). <c>Assigned</c>: <c>me</c>, <c>none</c> or a staff user id. <c>Q</c>: a number or a company name.</summary>
public sealed record StaffQuoteQuery(string? Status, string? Assigned, Guid? CompanyId, string? Q, int Page = 1, int PageSize = 20);

/// <summary>
/// An RFQ in the staff inbox. <c>SlaDueAt</c> is <c>Quotes:FirstQuoteSlaWorkingHours</c> of working time after it arrived, on the
/// Sunday-Thursday 09:00-18:00 Dhaka clock; <c>SlaWorkingMinutes</c> is the working time it waited (until the first quote was sent,
/// or until now); shown only, never enforced (§15 decision 12).
/// </summary>
public sealed record StaffRfqSummaryDto(Guid Id, string Number, string Status, string Source, Guid CompanyId, string CompanyName, Guid RequestedBy,
    string RequesterName, Guid? AssignedTo, string? AssigneeName, int LineCount, DateOnly? NeededBy, DateTime CreatedAt, DateTime? FirstQuoteSentAt,
    DateTime SlaDueAt, int SlaWorkingMinutes, bool SlaBreached, DateTime? ClosedAt, string? CloseReason, DateTime? WithdrawnAt);

public sealed record StaffQuoteRefDto(Guid Id, string Number, string Status);

public sealed record StaffRfqDetailDto(StaffRfqSummaryDto Rfq, string? Message, Guid? ShipToSiteId, IReadOnlyList<RfqLineDto> Lines,
    IReadOnlyList<StaffQuoteRefDto> Quotes);

public sealed record StaffRfqPage(IReadOnlyList<StaffRfqSummaryDto> Items, int Page, int PageSize, int Total);

public sealed record AssignRfqRequest(Guid? AssigneeId);

public sealed record StaffReasonRequest(string? Reason);

/// <summary>From an RFQ (its company, the requester as contact, its matched lines pre-filled) or from a company (a contact is then required).</summary>
public sealed record CreateStaffQuoteRequest(Guid? RfqId, Guid? CompanyId, Guid? ContactUserId);

/// <summary>
/// A quote in the staff lists. <c>Status</c> is computed: <c>closed</c>, or the latest version's status (<c>draft</c>,
/// <c>pending_approval</c>, <c>sent</c>, <c>expired</c>, <c>accepted</c>, <c>rejected</c>, <c>withdrawn</c>). Of the company's credit only
/// the terms and whether new credit orders are on hold, never balances or limits.
/// </summary>
public sealed record StaffQuoteSummaryDto(Guid Id, string Number, string Status, Guid CompanyId, string CompanyName, string CreditTerms, bool CreditOnHold,
    Guid? RfqId, string? RfqNumber, Guid ContactUserId, string ContactName, Guid? AssignedTo, string? AssigneeName, int LatestVersionNo,
    DateTime CreatedAt, DateTime UpdatedAt, DateTime? ClosedAt, string? CloseReason);

public sealed record StaffQuotePage(IReadOnlyList<StaffQuoteSummaryDto> Items, int Page, int PageSize, int Total);

public sealed record StaffQuoteFlagsDto(IReadOnlyList<Guid> BelowFloorLineIds, IReadOnlyList<Guid> OverAuthorityLineIds, bool LongValidity);

/// <summary>Cost figures of the last check; the whole object is absent without <c>pricing.view_cost</c>.</summary>
public sealed record StaffQuoteLineCostDto(long? CostMinorAtCheck, long? FloorMinorAtCheck);

public sealed record StaffQuoteLineDto(Guid Id, int LineNo, Guid VariantId, Guid? RfqLineId, string Sku, string? PartNumber, string TitleEn, string TitleBn,
    int Quantity, long UnitPriceMinor, decimal VatRate, long LineNetMinor, long LineTaxMinor, long LineTotalMinor, long PublicUnitPriceMinor,
    string? LineNote, string? BelowFloorReason, long? ReferenceUnitPriceMinor, string? ReferenceSource, bool BelowFloor, bool OverAuthority,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StaffQuoteLineCostDto? Cost);

/// <summary>One version as staff see it (all versions, drafts too). <c>RowVersion</c> is the token a draft edit sends back (D4.7).</summary>
public sealed record StaffQuoteVersionDto(Guid Id, int VersionNo, string Status, uint RowVersion, int ValidityDays, string PaymentTerms, string ShippingMode,
    Guid? ShipToSiteId, QuoteShipTo? ShipTo, string? TermsText, string? BuyerNote, string? InternalNote, string? PreparerName, string? ContentHash, StaffQuoteFlagsDto? Flags,
    long GoodsNetMinor, long TaxMinor, long GoodsTotalMinor, long? ShippingTotalMinor, long? GrandTotalMinor, Guid CreatedBy, IReadOnlyList<Guid> Contributors,
    Guid? SubmittedBy, DateTime? SubmittedAt, Guid? ApprovedBy, DateTime? ApprovedAt, string? ApprovalComment, Guid? ApprovalRejectedBy,
    DateTime? ApprovalRejectedAt, Guid? SentBy, DateTime? SentAt, DateTime? ValidUntil, Guid? AcceptedOrderId, DateTime? AcceptedAt,
    DateTime? RejectedAt, string? RejectReason, Guid? WithdrawnBy, DateTime? WithdrawnAt, string? WithdrawReason, DateTime? SupersededAt,
    DateTime CreatedAt, IReadOnlyList<StaffQuoteLineDto> Lines);

public sealed record StaffQuoteDetailDto(StaffQuoteSummaryDto Quote, IReadOnlyList<StaffQuoteVersionDto> Versions);

public sealed record QuoteShippingFeeInput(Guid SellerId, long ShippingMinor);

/// <summary>A line as staff write it: a catalogue variant, a quantity and an absolute VAT-exclusive unit price in whole poisha (§4).</summary>
public sealed record QuoteDraftLineInput(Guid VariantId, int Quantity, long UnitPriceMinor, string? LineNote, string? BelowFloorReason, Guid? RfqLineId);

/// <summary>
/// The whole content of a Draft (§12 <c>PUT</c>): header and every line, replacing what is stored; line numbers follow the list order.
/// <c>RowVersion</c>: the version's <c>rowVersion</c> from the last read (required by the save; a stale one is 409 <c>QUOTE_VERSION_CHANGED</c>;
/// the preview ignores it). <c>ContactUserId</c>: null keeps the contact. <c>PaymentTerms</c> <c>as_account</c> (default) or <c>advance</c>;
/// <c>ShippingMode</c> <c>calculated</c> (default) or <c>fixed</c> (then <c>ShipToSiteId</c>, a site of the quote's company, and the fees).
/// </summary>
public sealed record EditQuoteDraftRequest(uint? RowVersion, Guid? ContactUserId, int ValidityDays, string? PaymentTerms, string? ShippingMode,
    Guid? ShipToSiteId, IReadOnlyList<QuoteShippingFeeInput>? ShippingFees, string? TermsText, string? BuyerNote, string? InternalNote,
    IReadOnlyList<QuoteDraftLineInput>? Lines);

/// <summary>
/// One line of a price preview: the server's snapshot, the line money, and the staff check figures of now: the buyer's own resolver
/// price (<c>ReferenceUnitPriceMinor</c>, <c>ReferenceSource</c>), the flags, and cost and floor only with <c>pricing.view_cost</c>.
/// </summary>
public sealed record QuotePreviewLineDto(int LineNo, Guid VariantId, string Sku, string TitleEn, string TitleBn, int Quantity, long UnitPriceMinor,
    decimal VatRate, long LineNetMinor, long LineTaxMinor, long LineTotalMinor, long PublicUnitPriceMinor, long ReferenceUnitPriceMinor,
    string ReferenceSource, bool BelowFloor, bool OverAuthority, bool BelowCost, bool ReasonRequired,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] StaffQuoteLineCostDto? Cost);

/// <summary>
/// What a send of this content would do now (§5), computed and not saved. <c>Outcome</c>: <c>send</c> (no flag), <c>approval</c> (flags:
/// it would wait for a second person) or <c>refused</c>; <c>Problems</c> say why it would be refused: <c>NO_LINES</c>,
/// <c>SHIPPING_FEES_INCOMPLETE</c> (fixed delivery without a fee for every seller), <c>QUOTE_BELOW_COST</c>, <c>BELOW_FLOOR_REASON_REQUIRED</c>.
/// Line lists are line numbers. Shipping and grand total only for complete fixed delivery.
/// </summary>
public sealed record QuotePricePreviewDto(int VersionNo, string Outcome, IReadOnlyList<string> Problems, IReadOnlyList<int> BelowFloorLineNos,
    IReadOnlyList<int> OverAuthorityLineNos, bool LongValidity, IReadOnlyList<int> BelowCostLineNos, IReadOnlyList<int> ReasonRequiredLineNos,
    long GoodsNetMinor, long TaxMinor, long GoodsTotalMinor, long? ShippingTotalMinor, long? GrandTotalMinor, IReadOnlyList<QuotePreviewLineDto> Lines);

/// <summary>
/// The staff quote desk (ADR-0011 §3, §9, §12-§14; D4.6): the RFQ inbox, assignment and closing, and the quote lifecycle around
/// drafts (create, revise, close; reads of every version). Permissions are the API's (<c>quotes.view</c>, <c>quotes.edit</c>); the
/// conflict-of-interest refusal, the row locks (quotes → quote_versions, FOR NO KEY UPDATE), audit and outbox are here.
/// </summary>
public interface IStaffQuoteService
{
    Task<StaffRfqPage> ListRfqsAsync(StaffQuoteQuery query, CancellationToken ct = default);
    Task<StaffRfqDetailDto> GetRfqAsync(Guid rfqId, CancellationToken ct = default);
    Task<StaffRfqDetailDto> AssignRfqAsync(Guid rfqId, AssignRfqRequest request, CancellationToken ct = default);
    Task<StaffRfqDetailDto> CloseRfqAsync(Guid rfqId, StaffReasonRequest request, CancellationToken ct = default);
    Task<StaffQuoteDetailDto> CreateQuoteAsync(CreateStaffQuoteRequest request, CancellationToken ct = default);
    Task<StaffQuotePage> ListQuotesAsync(StaffQuoteQuery query, CancellationToken ct = default);
    Task<StaffQuoteDetailDto> GetQuoteAsync(Guid quoteId, CancellationToken ct = default);
    Task<StaffQuoteDetailDto> ReviseQuoteAsync(Guid quoteId, CancellationToken ct = default);
    Task<StaffQuoteDetailDto> CloseQuoteAsync(Guid quoteId, StaffReasonRequest request, CancellationToken ct = default);

    /// <summary>Replaces a Draft's content (D4.7): optimistic <c>RowVersion</c>, the editor becomes a contributor, a new contact must qualify.</summary>
    Task<StaffQuoteDetailDto> EditDraftAsync(Guid quoteId, int versionNo, EditQuoteDraftRequest request, CancellationToken ct = default);

    /// <summary>Totals, check figures and the flags a send of this content would raise now; nothing is saved (D4.7).</summary>
    Task<QuotePricePreviewDto> PreviewAsync(Guid quoteId, int versionNo, EditQuoteDraftRequest request, CancellationToken ct = default);
}
