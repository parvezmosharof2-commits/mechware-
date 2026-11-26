using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;

namespace Mechware.Application.Invoicing;

/// <summary>
/// ADR-0012 §2, §15 (E6.3c): issues the Mushak-6.3 of a shipment inside the ship transaction. The caller has locked the order
/// (<c>FOR NO KEY UPDATE</c>), checked its <c>rowVersion</c> and moved it to Shipped; <see cref="IssueAsync"/> then reads the buyer,
/// checks the Live refusals, writes the shipment, takes the counter as the LAST lock and inserts the invoice(s). The caller commits.
/// Off: nothing is issued (warning log). Specimen: <c>SPEC-…</c> numbers. Live: real numbers, or a 409 refusal
/// (<see cref="IsRefusal"/>) after which the caller rolls the whole ship back and calls <see cref="RecordRefusalAsync"/>.
/// </summary>
public interface IInvoiceIssuer
{
    /// <returns>The numbers issued (empty in Off mode), oldest series first.</returns>
    Task<IReadOnlyList<string>> IssueAsync(Order order, Shipment shipment, Guid issuedBy, CancellationToken ct = default);

    /// <summary>Writes the <c>invoice.issue_refused</c> audit row in its own transaction; call it only after the ship transaction rolled back.</summary>
    Task RecordRefusalAsync(Guid orderId, string orderNumber, ConflictException refusal, CancellationToken ct = default);

    /// <summary>The three Live refusals: 409 <c>INVOICE_SETUP_INCOMPLETE</c>, <c>INVOICE_SELLER_NOT_SUPPORTED</c>, <c>INVOICE_BUYER_BIN_INVALID</c>.</summary>
    static bool IsRefusal(ConflictException e) =>
        e.Code is InvoiceSnapshotBuilder.SetupIncomplete or InvoiceSnapshotBuilder.SellerNotSupported or InvoiceSnapshotBuilder.BuyerBinInvalid;
}
