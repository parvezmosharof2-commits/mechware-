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

    /// <summary>
    /// E6.5b: a 6.7 line crediting <paramref name="original"/> as planned (ADR-0012 §9.1): the original's kind, order line, texts and rates;
    /// quantity 0 (value only) … what was left; gross = net, no discount; the printed unit price is net / quantity (net for a value-only line),
    /// in the same column (exact or display) as the original's.
    /// </summary>
    internal static InvoiceLine Credit(Guid invoiceId, int lineNo, InvoiceLine original, PlannedCreditLine p)
    {
        if (p.OriginalLineId != original.Id || p.Quantity < 0 || p.Quantity > original.Quantity || p.NetMinor < 1 || p.VatMinor < 0 || original.SdMinor != 0)
            throw new InvalidOperationException($"Credit line {lineNo} does not fit line {original.LineNo} of the original.");
        var unit = (long)decimal.Round((decimal)p.NetMinor / Math.Max(p.Quantity, 1), 0, MidpointRounding.ToEven);
        var exact = original.UnitPriceMinor is not null;
        return new InvoiceLine
        {
            Id = Guid.NewGuid(),
            InvoiceId = invoiceId,
            LineNo = lineNo,
            Kind = original.Kind,
            OrderItemId = original.OrderItemId,
            OriginalLineId = original.Id,
            Sku = original.Sku,
            PartNumber = original.PartNumber,
            DescriptionEn = original.DescriptionEn,
            DescriptionBn = original.DescriptionBn,
            Unit = original.Unit,
            Quantity = p.Quantity,
            UnitPriceMinor = exact ? unit : null,
            DisplayUnitPriceMinor = exact ? null : unit,
            GrossMinor = p.NetMinor,
            NetMinor = p.NetMinor,
            SdRate = original.SdRate,
            VatRate = original.VatRate,
            VatMinor = p.VatMinor,
            TotalMinor = p.TotalMinor
        };
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

    /// <summary>The Dhaka calendar day the quote was sent (printed as "against quotation no. X dated D"); null on invoices issued before E6.4c and for orders without a quote.</summary>
    public DateOnly? QuoteDate { get; private set; }
    public string OrderNumber { get; private set; } = string.Empty;
    public string Currency { get; private set; } = "BDT";
    public long NetMinor { get; private set; }
    public long SdMinor { get; private set; }
    public long VatMinor { get; private set; }
    public long TotalMinor { get; private set; }
    public bool ConsultantChecked { get; private set; }
    public string ContentHash { get; private set; } = string.Empty;
    public Guid IssuedBy { get; private set; }

    /// <summary>Credit note only (E6.5b): the <see cref="CreditNoteReason"/> name and the cleaned text, both printed and hashed.</summary>
    public string? Reason { get; private set; }
    public string? ReasonText { get; private set; }

    /// <summary>Credit note only: who asked for it (never <see cref="IssuedBy"/>, the approver; DB CHECK).</summary>
    public Guid? RequestedBy { get; private set; }

    /// <summary>Credit note only: money owed back outside the credit ledger (a paid, non-credit order); 0 when the ledger took it.</summary>
    public long? RefundDueMinor { get; private set; }

    public IReadOnlyList<InvoiceLine> Lines => _lines.AsReadOnly();

    private Invoice() { }

    /// <summary>A tax invoice from its parts; the totals are plain sums of the lines and the hash is taken last, from this object only.</summary>
    internal Invoice(Guid id, InvoicingMode mode, string number, InvoiceSeries series, int fiscalYear, long serial, DateTime issuedAtUtc,
        Guid sellerId, Guid orderId, string orderNumber, Guid subOrderId, Guid shipmentId, Guid? companyId, InvoiceSellerSnapshot seller,
        InvoiceBuyerSnapshot buyer, InvoiceDeliverySnapshot delivery, string? purchaseOrderNumber, string? quoteNumber, string currency,
        bool consultantChecked, Guid issuedBy, IEnumerable<InvoiceLine> lines, DateOnly? quoteDate = null) : base(id)
    {
        _lines.AddRange(lines);
        if (_lines.Count == 0 || _lines.Any(l => l.InvoiceId != id)) throw new InvalidOperationException("An invoice needs its own lines.");
        (Kind, Mode, Number, Series, FiscalYear, Serial, IssuedAtUtc, CreatedAtUtc) =
            (InvoiceKind.TaxInvoice, mode, number, series, fiscalYear, serial, issuedAtUtc, issuedAtUtc);
        (SellerId, OrderId, OrderNumber, SubOrderId, ShipmentId, CompanyId, Seller, Buyer, Delivery) =
            (sellerId, orderId, orderNumber, subOrderId, shipmentId, companyId, seller, buyer, delivery);
        (PurchaseOrderNumber, QuoteNumber, Currency, ConsultantChecked, IssuedBy) = (purchaseOrderNumber, quoteNumber, currency, consultantChecked, issuedBy);
        QuoteDate = quoteDate;
        NetMinor = _lines.Sum(l => l.NetMinor);
        SdMinor = _lines.Sum(l => l.SdMinor);
        VatMinor = _lines.Sum(l => l.VatMinor);
        TotalMinor = _lines.Sum(l => l.TotalMinor);
        ContentHash = InvoiceContentHash.Compute(this);
    }

    /// <summary>
    /// E6.5b, ADR-0012 §9: the Mushak-6.7 of an approved <paramref name="plan"/> of <paramref name="original"/>. Everything printed is copied from
    /// the original (seller, buyer, delivery, PO, quote, order), never read live; the number is of the original's mode (series
    /// <c>CreditNote</c> or <c>SpecimenCreditNote</c>) with the original's prefix; lines follow the plan; the hash is taken last.
    /// </summary>
    public static Invoice CreditNote(Guid id, Invoice original, CreditNotePlan plan, InvoiceSeries series, int fiscalYear, long serial, DateTime issuedAtUtc,
        Guid requestedBy, Guid issuedBy, long refundDueMinor, bool consultantChecked)
    {
        ArgumentNullException.ThrowIfNull(original);
        ArgumentNullException.ThrowIfNull(plan);
        if (original.Kind != InvoiceKind.TaxInvoice || plan.OriginalInvoiceId != original.Id || plan.Lines.Count == 0)
            throw new InvalidOperationException("A credit note credits a tax invoice by its own plan.");
        if (series != (original.Mode == InvoicingMode.Live ? InvoiceSeries.CreditNote : InvoiceSeries.SpecimenCreditNote))
            throw new InvalidOperationException($"A credit note of a {original.Mode} invoice cannot take a number of the {series} series.");
        if (issuedAtUtc.Kind != DateTimeKind.Utc || serial < 1 || Invoicing.FiscalYear.Of(issuedAtUtc) != fiscalYear)
            throw new InvalidOperationException("The number does not belong to the issue instant.");
        if (requestedBy == issuedBy) throw new InvalidOperationException("The approver of a credit note is never its requester.");
        if (refundDueMinor < 0 || refundDueMinor > plan.TotalMinor) throw new InvalidOperationException("The refund due is 0 … the credit note's total.");
        var prefix = original.Number[..original.Number.IndexOf('-', StringComparison.Ordinal)];
        var note = new Invoice
        {
            Id = id,
            Kind = InvoiceKind.CreditNote,
            Mode = original.Mode,
            Series = series,
            FiscalYear = fiscalYear,
            Serial = serial,
            // A specimen prints SPEC whatever the live prefix is, so any valid one will do there.
            Number = InvoiceNumber.Format(prefix == InvoiceNumber.SpecimenPrefix ? "MW" : prefix, series, fiscalYear, serial),
            IssuedAtUtc = issuedAtUtc,
            CreatedAtUtc = issuedAtUtc,
            SellerId = original.SellerId,
            OrderId = original.OrderId,
            OrderNumber = original.OrderNumber,
            SubOrderId = original.SubOrderId,
            OriginalInvoiceId = original.Id,
            CompanyId = original.CompanyId,
            Seller = original.Seller,
            Buyer = original.Buyer,
            Delivery = original.Delivery,
            PurchaseOrderNumber = original.PurchaseOrderNumber,
            QuoteNumber = original.QuoteNumber,
            Currency = original.Currency,
            ConsultantChecked = consultantChecked,
            IssuedBy = issuedBy,
            Reason = plan.Reason.ToString(),
            ReasonText = plan.ReasonText,
            RequestedBy = requestedBy,
            RefundDueMinor = refundDueMinor
        };
        note._lines.AddRange(plan.Lines.Select((p, n) => InvoiceLine.Credit(id, n + 1, original.Lines.Single(l => l.Id == p.OriginalLineId), p)));
        (note.NetMinor, note.SdMinor, note.VatMinor, note.TotalMinor) =
            (note._lines.Sum(l => l.NetMinor), 0, note._lines.Sum(l => l.VatMinor), note._lines.Sum(l => l.TotalMinor));
        note.ContentHash = InvoiceContentHash.Compute(note);
        return note;
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
