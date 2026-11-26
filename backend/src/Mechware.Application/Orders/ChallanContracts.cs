namespace Mechware.Application.Orders;

/// <summary>A finished delivery challan PDF to send. Rendered on demand; nothing is stored.</summary>
public sealed record ChallanPdfFile(byte[] Bytes, string FileName);

/// <summary>
/// The delivery challan (E6.4d): the goods list that travels with a shipment, no prices. It is not a tax document, so it is rendered from the
/// order and shipment on every request and never stored. Only for an order that has shipped (409 <c>CHALLAN_NOT_AVAILABLE</c> before). Whatever
/// the caller may not see is one and the same <c>KeyNotFoundException</c>.
/// </summary>
public interface IChallanPdfService
{
    /// <summary>The buyer's challan: the order's placer, or a company Admin/Buyer (<c>Credit:BalanceVisibleTo</c>) of the order's company.</summary>
    Task<ChallanPdfFile> BuyerAsync(Guid actorUserId, Guid orderId, string? locale, CancellationToken ct = default);

    /// <summary>The staff challan of any shipped order (the route checks the permission).</summary>
    Task<ChallanPdfFile> StaffAsync(Guid orderId, string? locale, CancellationToken ct = default);
}
