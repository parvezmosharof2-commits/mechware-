using System.Globalization;
using Mechware.Domain.Common.Primitives;

namespace Mechware.Domain.Invoicing;

/// <summary>Mushak-6.3 (tax invoice) or Mushak-6.7 (credit note, E6.5): one table for both kinds (ADR-0012 §16).</summary>
public enum InvoiceKind
{
    TaxInvoice,
    CreditNote
}

/// <summary>A goods line copied from an order line, or the one delivery line of the sub-order's shipping (ADR-0012 §7).</summary>
public enum InvoiceLineKind
{
    Goods,
    Delivery
}

/// <summary>The issuer facts of §3 as they were at the issue instant. Null = not configured (a specimen prints "‹missing›").</summary>
public sealed record InvoiceSellerSnapshot(
    string? LegalNameEn, string? LegalNameBn, string? Bin, string? Tin, string? TradeLicence, string? RegisteredAddressEn, string? RegisteredAddressBn,
    string? PremisesAddressEn, string? PremisesAddressBn, string? ResponsiblePersonName, string? ResponsiblePersonDesignation);

/// <summary>The buyer as printed (§6): the company's legal name, BIN and billing address, or a person's name and ship-to address without a BIN.</summary>
public sealed record InvoiceBuyerSnapshot(string Name, string? Bin, string? Address);

/// <summary>The order's ship-to address without its ids, district link or phone (only what the form prints).</summary>
public sealed record InvoiceAddress(
    string? SiteName, string? RecipientName, string? Line1, string? Line2, string? Area, string? Upazila, string? District, string? Division, string? PostalCode)
{
    public string OneLine() => string.Join(", ", new[] { Line1, Line2, Area, Upazila, District, Division, PostalCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
}

/// <summary>Where the goods went and how: the ship-to address and the courier + tracking number as "vehicle / transport" (§6, §19 Q4).</summary>
public sealed record InvoiceDeliverySnapshot(InvoiceAddress? ShipTo, string CourierName, string TrackingNumber);

/// <summary>
/// One printed line (ADR-0012 §7, §16). Money is the order line's money exactly (I4). <see cref="UnitPriceMinor"/> is the exact unit
/// price excl. VAT of a company (net-basis) order; a personal order stores a VAT-inclusive unit price, so it prints
/// <see cref="DisplayUnitPriceMinor"/> = net / quantity rounded to the poisha (banker's), for display only: no value is computed from it.
/// </summary>
public sealed class InvoiceLine : Entity<Guid>
{
    public Guid InvoiceId { get; private set; }
    public int LineNo { get; private set; }
    public InvoiceLineKind Kind { get; private set; }
    public Guid? OrderItemId { get; private set; }
    public Guid? OriginalLineId { get; private set; }
    public string? Sku { get; private set; }
    public string? PartNumber { get; private set; }
    public string DescriptionEn { get; private set; } = string.Empty;
    public string DescriptionBn { get; private set; } = string.Empty;
    public string Unit { get; private set; } = "pcs";
    public int Quantity { get; private set; }
    public long? UnitPriceMinor { get; private set; }
    public long? DisplayUnitPriceMinor { get; private set; }
    public long GrossMinor { get; private set; }
    public long DiscountMinor { get; private set; }
    public long NetMinor { get; private set; }
    public decimal SdRate { get; private set; }
    public long SdMinor { get; private set; }
    public decimal VatRate { get; private set; }
    public long VatMinor { get; private set; }
    public long TotalMinor { get; private set; }

    private InvoiceLine() { }

    internal InvoiceLine(Guid invoiceId, int lineNo, InvoiceLineKind kind, Guid? orderItemId, string? sku, string descriptionEn, string descriptionBn,
        int quantity, long? unitPriceMinor, long? displayUnitPriceMinor, long gross, long discount, long net, decimal vatRate, long vat, long total)
        : base(Guid.NewGuid())
    {
        if (net + vat != total || net < 0 || vat < 0 || discount < 0 || gross < 0 || quantity < 1)
            throw new InvalidOperationException($"Invoice line {lineNo}: total must be net + VAT and no amount may be negative.");
        (InvoiceId, LineNo, Kind, OrderItemId, Sku, DescriptionEn, DescriptionBn, Quantity) =
            (invoiceId, lineNo, kind, orderItemId, sku, descriptionEn, descriptionBn, quantity);
        (UnitPriceMinor, DisplayUnitPriceMinor, GrossMinor, DiscountMinor, NetMinor, VatRate, VatMinor, TotalMinor) =
            (unitPriceMinor, displayUnitPriceMinor, gross, discount, net, vatRate, vat, total);
    }
}

/// <summary>
/// An issued Mushak-6.3 (ADR-0012 §1, §6): everything printed is copied in at the issue instant and never read live again, and the
/// <see cref="ContentHash"/> of that content is fixed at the same moment. Nothing changes it afterwards (rule I2; the E6.3b guard
/// trigger is the database's half). Built only by <see cref="InvoiceSnapshotBuilder"/>.
/// </summary>
public sealed class Invoice : Entity<Guid>
{
    private readonly List<InvoiceLine> _lines = [];

    public InvoiceKind Kind { get; private set; }
    public InvoicingMode Mode { get; private set; }
    public string Number { get; private set; } = string.Empty;
    public Guid SellerId { get; private set; }
    public InvoiceSeries Series { get; private set; }
    public int FiscalYear { get; private set; }
    public long Serial { get; private set; }
    public DateTime IssuedAtUtc { get; private set; }
    public Guid OrderId { get; private set; }
    public Guid SubOrderId { get; private set; }
    public Guid? ShipmentId { get; private set; }
    public int ReplacementNo { get; private set; }
    public Guid? OriginalInvoiceId { get; private set; }
    public Guid? CompanyId { get; private set; }
    public InvoiceSellerSnapshot Seller { get; private set; } = null!;
    public InvoiceBuyerSnapshot Buyer { get; private set; } = null!;
    public InvoiceDeliverySnapshot Delivery { get; private set; } = null!;
    public string? PurchaseOrderNumber { get; private set; }
    public string? QuoteNumber { get; private set; }
    public string OrderNumber { get; private set; } = string.Empty;
    public string Currency { get; private set; } = "BDT";
    public long NetMinor { get; private set; }
    public long SdMinor { get; private set; }
    public long VatMinor { get; private set; }
    public long TotalMinor { get; private set; }
    public bool ConsultantChecked { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public Guid IssuedBy { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines.AsReadOnly();

    private Invoice() { }

    /// <summary>A tax invoice from its parts; the totals are plain sums of the lines and the hash is taken last, from this object only.</summary>
    internal Invoice(Guid id, InvoicingMode mode, string number, InvoiceSeries series, int fiscalYear, long serial, DateTime issuedAtUtc,
        Guid sellerId, Guid orderId, string orderNumber, Guid subOrderId, Guid shipmentId, Guid? companyId, InvoiceSellerSnapshot seller,
        InvoiceBuyerSnapshot buyer, InvoiceDeliverySnapshot delivery, string? purchaseOrderNumber, string? quoteNumber, string currency,
        bool consultantChecked, Guid issuedBy, IEnumerable<InvoiceLine> lines) : base(id)
    {
        _lines.AddRange(lines);
        if (_lines.Count == 0 || _lines.Any(l => l.InvoiceId != id)) throw new InvalidOperationException("An invoice needs its own lines.");
        (Kind, Mode, Number, Series, FiscalYear, Serial, IssuedAtUtc, CreatedAtUtc) =
            (InvoiceKind.TaxInvoice, mode, number, series, fiscalYear, serial, issuedAtUtc, issuedAtUtc);
        (SellerId, OrderId, OrderNumber, SubOrderId, ShipmentId, CompanyId, Seller, Buyer, Delivery) =
            (sellerId, orderId, orderNumber, subOrderId, shipmentId, companyId, seller, buyer, delivery);
        (PurchaseOrderNumber, QuoteNumber, Currency, ConsultantChecked, IssuedBy) = (purchaseOrderNumber, quoteNumber, currency, consultantChecked, issuedBy);
        NetMinor = _lines.Sum(l => l.NetMinor);
        SdMinor = _lines.Sum(l => l.SdMinor);
        VatMinor = _lines.Sum(l => l.VatMinor);
        TotalMinor = _lines.Sum(l => l.TotalMinor);
        ContentHash = InvoiceContentHash.Compute(this);
    }

    /// <summary>The printed date and time: Dhaka local (+06:00), <c>dd-MM-yyyy HH:mm</c> (ADR-0012 §5); the stored value stays UTC.</summary>
    public string PrintedIssueTime => InvoiceDates.Printed(IssuedAtUtc);
}

/// <summary>Dhaka calendar views of an issue instant (ADR-0012 §5, §13): the printed time and the Mushak-9.1 return month.</summary>
public static class InvoiceDates
{
    public static string Printed(DateTime utc) =>
        DateTime.SpecifyKind(utc, DateTimeKind.Utc).Add(Invoicing.FiscalYear.DhakaOffset).ToString("dd-MM-yyyy HH:mm", CultureInfo.InvariantCulture);

    /// <summary>The Dhaka calendar month the 9.1 return of this instant belongs to.</summary>
    public static (int Year, int Month) ReturnMonth(DateTime utc)
    {
        var d = Invoicing.FiscalYear.DhakaDate(utc);
        return (d.Year, d.Month);
    }
}
