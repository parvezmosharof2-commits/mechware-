using Mechware.Application.Catalog;
using Mechware.Domain.Invoicing;

namespace Mechware.Application.Invoicing;

/// <summary>
/// One invoice or credit note in a list (E6.8a). <paramref name="Kind"/> is <c>tax_invoice</c> or <c>credit_note</c>, <paramref name="Mode"/> is
/// <c>live</c> or <c>specimen</c>. Money in poisha. The original is set on a credit note only.
/// </summary>
public sealed record InvoiceSummaryDto(Guid Id, string Number, string Kind, string Mode, DateTime IssuedAtUtc, Guid OrderId, string OrderNumber,
    Guid? OriginalInvoiceId, string? OriginalNumber, string Currency, long NetMinor, long SdMinor, long VatMinor, long TotalMinor);

/// <summary>A printed line: money exactly as on the form. No order-item id, no original-line id.</summary>
public sealed record InvoiceLineDto(int LineNo, string Kind, string? Sku, string? PartNumber, string DescriptionEn, string DescriptionBn, string Unit, int Quantity,
    long? UnitPriceMinor, long? DisplayUnitPriceMinor, long GrossMinor, long DiscountMinor, long NetMinor, decimal SdRate, long SdMinor, decimal VatRate, long VatMinor, long TotalMinor);

/// <summary>The buyer as printed. <paramref name="Bin"/> is masked (last four digits) for a buyer, complete for staff.</summary>
public sealed record InvoiceBuyerDto(string Name, string? Bin, string? Address);

/// <summary>What a buyer (the order's placer, or a company Admin/Buyer) sees of an invoice: the printed content, never staff ids, cost, notes, the hash or ciphertext.</summary>
public sealed record BuyerInvoiceDetailDto(InvoiceSummaryDto Invoice, InvoiceSellerSnapshot Seller, InvoiceBuyerDto Buyer, InvoiceDeliverySnapshot Delivery,
    string? PurchaseOrderNumber, string? QuoteNumber, IReadOnlyList<InvoiceLineDto> Lines);

/// <summary>A row of the staff list: the summary and who it was issued to.</summary>
public sealed record StaffInvoiceListItemDto(InvoiceSummaryDto Invoice, Guid SellerId, Guid? CompanyId, string BuyerName);

/// <summary>The staff view: the buyer detail with the whole BIN, plus the content hash, series, serial and the issuing user and rows.</summary>
public sealed record StaffInvoiceDetailDto(BuyerInvoiceDetailDto Detail, string Series, int FiscalYear, long Serial, Guid SellerId, Guid? CompanyId, Guid SubOrderId,
    Guid? ShipmentId, bool ConsultantChecked, string ContentHash, Guid IssuedBy);

/// <summary>Staff list filters: <c>number</c> and <c>orderNumber</c> are prefixes; <c>from</c>/<c>to</c> are inclusive Dhaka dates (yyyy-MM-dd); <c>kind</c>, <c>mode</c> are wire names.</summary>
public sealed record StaffInvoiceFilter(string? Number, string? OrderNumber, Guid? CompanyId, string? From, string? To, string? Kind, string? Mode, int Page, int PageSize);

/// <summary>
/// Buyer reads (E6.8a). Every refusal (unknown id, another person's order or company, a role that may not see invoices, a Pending or Rejected
/// company) throws the same <see cref="KeyNotFoundException"/> ("Invoice not found."). READ-ONLY: one snapshot, no lock.
/// </summary>
public interface IBuyerInvoiceReadService
{
    public const int MaxPageSize = 50;

    /// <summary>The invoices and credit notes of an order the caller placed, oldest first.</summary>
    Task<IReadOnlyList<InvoiceSummaryDto>> ListForOrderAsync(Guid userId, Guid orderId, CancellationToken ct = default);

    Task<BuyerInvoiceDetailDto> GetForOrderAsync(Guid userId, Guid orderId, Guid invoiceId, CancellationToken ct = default);

    /// <summary>The company's invoices, newest first, for a member whose role is in <c>Credit:BalanceVisibleTo</c> (Admin, Buyer by default).</summary>
    Task<PagedResult<InvoiceSummaryDto>> ListForCompanyAsync(Guid userId, Guid companyId, string? from, string? to, int page, int pageSize, CancellationToken ct = default);

    Task<BuyerInvoiceDetailDto> GetForCompanyAsync(Guid userId, Guid companyId, Guid invoiceId, CancellationToken ct = default);
}

/// <summary>Staff reads (E6.8a), behind <c>invoices.view</c>. An unknown id is a 404; a bad filter is a 400.</summary>
public interface IStaffInvoiceReadService
{
    Task<PagedResult<StaffInvoiceListItemDto>> ListAsync(StaffInvoiceFilter filter, CancellationToken ct = default);

    Task<StaffInvoiceDetailDto> GetAsync(Guid invoiceId, CancellationToken ct = default);
}
