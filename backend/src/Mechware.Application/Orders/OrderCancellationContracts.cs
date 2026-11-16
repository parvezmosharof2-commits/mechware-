namespace Mechware.Application.Orders;

/// <summary>Why staff cancel an order that is already confirmed: 5 to 500 characters, recorded on the order event, the audit row and the ledger.</summary>
public sealed record CancelConfirmedOrderRequest(string? Reason);

/// <summary>One variant's stock this call put back on the shelf.</summary>
public sealed record ReturnedStockDto(Guid VariantId, int Quantity);

/// <summary>
/// What this call did (E7.2). <paramref name="AlreadyCancelled"/>: the order was cancelled before this call, so it changed neither
/// the order, the credit nor the payments; it only finished a stock return that had not run. <paramref name="CreditReleasedMinor"/>
/// is what the company owes less; <paramref name="CreditMovedOnAccountMinor"/> is money it had already paid, now on account.
/// <paramref name="RefundPaymentIds"/> are the gateway payments now waiting in the refund queue (four eyes, AdminRefunds).
/// <paramref name="StockReturnPending"/>: a stock return failed after the cancel committed; calling again finishes it (E7.3 sweeps too).
/// </summary>
public sealed record CancelledOrderDto(
    Guid OrderId, string OrderNumber, string Status, bool AlreadyCancelled, long CreditReleasedMinor, long CreditMovedOnAccountMinor,
    IReadOnlyList<Guid> RefundPaymentIds, IReadOnlyList<ReturnedStockDto> StockReturned, bool StockReturnPending);

public interface IOrderCancellationService
{
    /// <summary>
    /// Staff cancel a confirmed, processing or packed order (E7.2; nothing has left the warehouse). In one transaction: the order
    /// becomes cancelled (event + reason), a credit order's charge is reduced through <c>ICreditLedger.ReduceChargeAsync</c>, captured
    /// gateway payments are flagged for refund, promotion uses are given back, one audit row and one outbox event are written. After the
    /// commit the deducted stock goes back to the warehouses it came from. Calling it again for a cancelled order changes nothing but
    /// finishes the stock return. 404 unknown order, 409 <c>ORDER_NOT_CANCELLABLE</c> for any other state, 400 for a bad reason.
    /// </summary>
    Task<CancelledOrderDto> CancelConfirmedAsync(Guid orderId, CancelConfirmedOrderRequest request, CancellationToken ct = default);
}
