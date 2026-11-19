using Mechware.Domain.Common;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Quotes;

/// <summary>Where an RFQ's lines came from (ADR-0011 §2). Stored as text; values never renumber.</summary>
public enum RfqSource { Cart = 1, Text = 2, Csv = 3, Product = 4 }

/// <summary>Stored RFQ status (§2): Submitted → Quoting → Answered → Closed, or Withdrawn by the buyer.</summary>
public enum RfqStatus { Submitted = 1, Quoting = 2, Answered = 3, Closed = 4, Withdrawn = 5 }

/// <summary>How a requested line matched the catalogue (D5.2). A variant is recorded iff Matched or Obsolete.</summary>
public enum RfqMatchStatus { Matched = 1, Ambiguous = 2, NotFound = 3, Obsolete = 4 }

/// <summary>Stored version status (§3). "Expired" is computed from the clock (<see cref="QuoteVersion.IsExpired"/>), never stored.</summary>
public enum QuoteVersionStatus { Draft = 1, PendingApproval = 2, Sent = 3, Accepted = 4, Rejected = 5, Withdrawn = 6, Superseded = 7 }

/// <summary>"As your account allows" or "advance payment only" (§15 decision 11). A quote never grants credit terms.</summary>
public enum QuotePaymentTerms { AsAccount = 1, Advance = 2 }

/// <summary>Calculated at acceptance by the normal quoter, or fixed per seller to one named site (§8.3).</summary>
public enum QuoteShippingMode { Calculated = 1, Fixed = 2 }

/// <summary>Hard limits of ADR-0011 §2, §4, §11 (the database CHECKs use the same numbers).</summary>
public static class QuoteLimits
{
    /// <summary>= <c>PlaceOrderRequest.MaxLines</c>, so a version can always become one order (§4).</summary>
    public const int MaxLinesPerVersion = 50;
    public const int MaxQuantityPerLine = OrderLimits.MaxQuantityPerLine;
    public const int MaxRfqLines = 500;
    public const int MinReasonLength = 5, MaxReasonLength = 500;
    public const int MaxMessageLength = 2_000, MaxRawTextLength = 200, MaxPartKeyLength = 64, MaxRfqNoteLength = 500;
    public const int MaxLineNoteLength = 300, MaxTermsLength = 4_000, MaxNoteLength = 2_000, MaxRejectReasonLength = 1_000;
    public const int MaxNameLength = 200, MaxSkuLength = 64, MaxTitleLength = 255;
    public const int MaxNeededByDaysAhead = 365;
}

/// <summary>The catalogue facts of a line's variant at this instant, read by the caller (§4 snapshot).</summary>
/// <param name="QuantityProblem"><c>ProductVariant.QuantityProblem</c> for the line's quantity (MOQ, pack); null when it may be ordered.</param>
/// <param name="VatRate">The category's rate the PDF shows, 0 … 1 with at most 4 decimals.</param>
/// <param name="PublicUnitPriceMinor">The public price on the net basis (<c>PriceResolver.ToOrderBasis</c>).</param>
public sealed record QuoteLineFacts(Guid VariantId, bool Active, Guid SellerId, string Sku, string? PartNumber, string TitleEn, string TitleBn,
    decimal VatRate, long PublicUnitPriceMinor, string? QuantityProblem);

/// <summary>One line as staff write it: an absolute VAT-exclusive unit price in whole poisha (§4).</summary>
public sealed record QuoteLineDraft(Guid VariantId, int Quantity, long UnitPriceMinor, QuoteLineFacts Facts, string? LineNote = null,
    string? BelowFloorReason = null, Guid? RfqLineId = null);

public sealed record QuoteShippingFee(Guid SellerId, long ShippingMinor);

/// <summary>The whole editable content of a Draft (header and all lines), replaced at once (D4.7 <c>PUT</c>).</summary>
public sealed record QuoteDraft(int ValidityDays, QuotePaymentTerms PaymentTerms, QuoteShippingMode ShippingMode, Guid? ShipToSiteId,
    IReadOnlyList<QuoteShippingFee>? ShippingFees, string? TermsText, string? BuyerNote, string? InternalNote, IReadOnlyList<QuoteLineDraft> Lines);

/// <summary>The buyer company as the PDF prints it, frozen when the version leaves Draft.</summary>
public sealed record QuoteCompanySnapshot(string Name, string? Bin, string? BillingAddress);

/// <summary>Approval flags of ADR-0011 §5, computed by <see cref="QuoteApprovalPolicy"/>; any flag sends the version to PendingApproval.</summary>
public sealed record QuoteFlags(IReadOnlyList<Guid> BelowFloorLineIds, IReadOnlyList<Guid> OverAuthorityLineIds, bool LongValidity)
{
    public static readonly QuoteFlags None = new([], [], false);
    public bool NeedsApproval => BelowFloorLineIds.Count > 0 || OverAuthorityLineIds.Count > 0 || LongValidity;
}

/// <summary>
/// What the server fixes when a version leaves Draft (§4, §6): fresh line facts, company snapshot, preparer, and the policy run
/// of that moment. The content hash is not an input: the domain computes it (<see cref="QuoteContentHash"/>).
/// </summary>
public sealed record QuoteIssue(QuoteCompanySnapshot Company, string PreparerName, IReadOnlyList<QuoteLineFacts> Facts, QuoteApprovalCheck Check);

/// <summary>
/// A staff user acting on a quote, with the companies they are an effective member of, read from <c>company_members</c> in the
/// same request (never from the body). A member of the quote's company may not edit, send or approve it (§5, QT6).
/// </summary>
public sealed record QuoteActor(Guid UserId, IReadOnlyCollection<Guid> CompanyIds);

/// <summary>
/// What the approver sends back (§5, §12): the content hash they were shown and exactly the flagged line ids they acknowledge.
/// <paramref name="MayApproveBelowFloor"/> = the approver holds <c>quotes.approve_below_floor</c>.
/// </summary>
public sealed record QuoteApproval(bool MayApproveBelowFloor, string ContentHash, IReadOnlyCollection<Guid> BelowFloorLineIds,
    IReadOnlyCollection<Guid> OverAuthorityLineIds, string? Comment);

// Outbox events (§14): ids only; the publisher reads recipients and values at send time.
public sealed record RfqSubmittedEvent(Guid RfqId, Guid CompanyId) : DomainEventBase;
public sealed record QuoteApprovalRequestedEvent(Guid QuoteId, Guid VersionId, int VersionNo) : DomainEventBase;
public sealed record QuoteSentEvent(Guid QuoteId, Guid VersionId, int VersionNo) : DomainEventBase;
public sealed record QuoteAcceptedEvent(Guid QuoteId, Guid VersionId, int VersionNo, Guid OrderId) : DomainEventBase;
public sealed record QuoteRejectedEvent(Guid QuoteId, Guid VersionId, int VersionNo) : DomainEventBase;
public sealed record QuoteWithdrawnEvent(Guid QuoteId, Guid VersionId, int VersionNo) : DomainEventBase;
