using Mechware.Domain.Quotes;

namespace Mechware.Application.Quotes;

/// <summary>One line of a sent quote version as the order takes it (ADR-0011 §8.3): the frozen snapshot, never the catalogue's or the client's.</summary>
/// <param name="UnitPriceMinor">The quoted VAT-exclusive unit price in whole poisha.</param>
/// <param name="PublicUnitPriceMinor">The public price on the net basis at the send; the order line records it as its public price.</param>
public sealed record QuoteOrderLine(Guid LineId, Guid VariantId, Guid SellerId, string Sku, string TitleEn, int Quantity, long UnitPriceMinor,
    long PublicUnitPriceMinor);

/// <summary>
/// A sent quote version the caller may accept, as the order path needs it (§8.3): its lines, payment terms and delivery. Fixed delivery
/// carries the quoted site, its address as sent and one fee per seller; calculated delivery carries none of them.
/// </summary>
public sealed record QuoteForOrder(Guid QuoteId, string Number, Guid CompanyId, Guid VersionId, int VersionNo, QuotePaymentTerms PaymentTerms,
    QuoteShippingMode ShippingMode, Guid? ShipToSiteId, QuoteShipTo? ShipTo, IReadOnlyList<QuoteShippingFee> ShippingFees, IReadOnlyList<QuoteOrderLine> Lines);

/// <summary>
/// The only way the order side reaches quotes (ADR-0011 §8.3, §9), so no order code reads or writes a quote row itself. The refusals are
/// the quote domain's: 409 <c>QUOTE_NOT_ACCEPTABLE</c> (expired, withdrawn, rejected, superseded or closed), <c>QUOTE_ALREADY_ACCEPTED</c>
/// and <c>QUOTE_CHANGED</c> (another content hash).
/// </summary>
public interface IQuoteAcceptanceGate
{
    /// <summary>
    /// The version as <paramref name="userId"/> may accept it, read without a lock (§8.2, §13): an effective member of
    /// <paramref name="companyId"/>, the quote's own company, who may see this quote. Anything else, a draft or a version waiting for
    /// approval included, is one 404 ("Quote not found."); a member who may see it without the Buyer role is 403 <c>COMPANY_ROLE_REQUIRED</c>.
    /// </summary>
    Task<QuoteForOrder> FindAsync(Guid userId, Guid companyId, Guid quoteId, int versionNo, CancellationToken ct = default);

    /// <summary>The early check (§8.3 step 4), without a lock and before any stock is held: acceptable (§8.6), valid at <paramref name="nowUtc"/> (§7), the hash the buyer saw.</summary>
    Task EnsureAcceptableAsync(QuoteForOrder quote, string contentHash, Guid userId, DateTime nowUtc, CancellationToken ct = default);

    /// <summary>
    /// Inside the caller's transaction and before any order, credit or company row lock (§8.3 step 5, §9): locks the quote row, then its
    /// versions, FOR NO KEY UPDATE, decides again with the same instant, marks the version accepted by <paramref name="orderId"/> and adds the
    /// audit row <c>quote.accepted</c> (the outbox event comes from the domain). The caller's save writes it with the order, or nothing.
    /// </summary>
    Task AcceptAsync(QuoteForOrder quote, string contentHash, Guid orderId, Guid userId, DateTime nowUtc, CancellationToken ct = default);
}
