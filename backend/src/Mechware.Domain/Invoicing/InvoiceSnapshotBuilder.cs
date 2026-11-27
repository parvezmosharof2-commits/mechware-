using System.Text.Json;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;

namespace Mechware.Domain.Invoicing;

/// <summary>
/// What the issuer read inside the ship transaction (ADR-0012 §2, §6): the shipped order, the issuing seller's sub-order, the shipment,
/// the buyer company and its billing site (company orders), the person's display name (personal orders), the quote number and its Dhaka send date, and the
/// number just taken (E6.2: series, fiscal year, serial, database issue instant).
/// </summary>
public sealed record InvoiceIssue(
    Guid InvoiceId, Order Order, Guid SellerId, Shipment Shipment, Company? Company, CompanySite? BillingSite, string? BuyerDisplayName,
    string? QuoteNumber, InvoiceSeries Series, int FiscalYear, long Serial, DateTime IssuedAtUtc, Guid IssuedBy, DateOnly? QuoteDate = null);

/// <summary>
/// Builds the Mushak-6.3 snapshot (ADR-0012 §6, §7, threat model IT7, IT8). Everything printed is copied out of the inputs here, so a
/// later change of the company, its site or the configuration never reaches an issued invoice. Line money is the order line's money
/// exactly (I4): nothing is recomputed, the one delivery line only splits the sub-order's shipping, and the only derived number is a
/// personal order's displayed unit price.
/// </summary>
public static class InvoiceSnapshotBuilder
{
    public const string DeliveryDescriptionEn = "Delivery charge";
    public const string DeliveryDescriptionBn = "ডেলিভারি চার্জ";

    public static Invoice BuildTaxInvoice(InvoiceIssue issue, InvoicingOptions options)
    {
        ArgumentNullException.ThrowIfNull(issue);
        ArgumentNullException.ThrowIfNull(options);
        var (order, mode) = (issue.Order, options.EffectiveMode);
        if (mode == InvoicingMode.Off) throw new InvalidOperationException("Invoicing is Off: no invoice is issued.");
        if (issue.Series != (mode == InvoicingMode.Live ? InvoiceSeries.TaxInvoice : InvoiceSeries.SpecimenTaxInvoice))
            throw new InvalidOperationException($"A {mode} tax invoice cannot take a number of the {issue.Series} series.");
        if (issue.IssuedAtUtc.Kind != DateTimeKind.Utc || issue.Serial < 1 || FiscalYear.Of(issue.IssuedAtUtc) != issue.FiscalYear)
            throw new InvalidOperationException("The number does not belong to the issue instant.");
        if (order.Status != OrderStatus.Shipped || issue.Shipment.OrderId != order.Id)
            throw new InvalidOperationException("A tax invoice is issued for the shipment of a shipped order.");
        var sub = order.SubOrders.SingleOrDefault(s => s.SellerId == issue.SellerId)
            ?? throw new InvalidOperationException("The order has no sub-order of this seller.");
        if (mode == InvoicingMode.Live) EnsureLiveIssuable(options, issue.SellerId, order, issue.Company);

        var shipTo = ShipTo(order.ShipToJson);
        return new Invoice(issue.InvoiceId, mode, InvoiceNumber.Format(options.NumberPrefix, issue.Series, issue.FiscalYear, issue.Serial),
            issue.Series, issue.FiscalYear, issue.Serial, issue.IssuedAtUtc, issue.SellerId, order.Id, order.OrderNumber, sub.Id, issue.Shipment.Id,
            order.CompanyId, options.SellerSnapshot(), Buyer(issue, shipTo),
            new InvoiceDeliverySnapshot(shipTo, issue.Shipment.CourierName, issue.Shipment.TrackingNumber),
            order.PurchaseOrderNumber, Blank(issue.QuoteNumber), order.Currency, options.ConsultantChecked, issue.IssuedBy,
            Lines(issue.InvoiceId, order.TaxMode, sub, options.ShippingVatRate), Blank(issue.QuoteNumber) is null ? null : issue.QuoteDate);
    }

    /// <summary>
    /// The Live refusals (ADR-0012 §2, §6), in this order: issuer facts missing → 409 <c>INVOICE_SETUP_INCOMPLETE</c>; a seller MECHWARE
    /// does not invoice for → 409 <c>INVOICE_SELLER_NOT_SUPPORTED</c>; a company order whose company is not active or has no valid BIN →
    /// 409 <c>INVOICE_BUYER_BIN_INVALID</c> (no silent fallback to "unregistered"). The issuer (E6.3c) calls it before the counter lock,
    /// so a refused ship never touches the counter; <see cref="BuildTaxInvoice"/> calls it again.
    /// </summary>
    public static void EnsureLiveIssuable(InvoicingOptions options, Guid sellerId, Order order, Company? company)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(order);
        if (options.MissingIssuerFacts() is { Count: > 0 } missing)
            throw new ConflictException(SetupIncomplete, $"Invoicing is not set up: {string.Join(", ", missing)} missing.");
        if (sellerId != options.IssuerSellerId)
            throw new ConflictException(SellerNotSupported, "MECHWARE does not issue invoices for this seller yet.");
        if (order.CompanyId is not null && company is not null && (!company.IsActive || !TaxIdFormat.IsBin(Blank(company.Bin))))
            throw new ConflictException(BuyerBinInvalid, "The buyer company must be active with a valid BIN before a tax invoice is issued.");
    }

    public const string SetupIncomplete = "INVOICE_SETUP_INCOMPLETE";
    public const string SellerNotSupported = "INVOICE_SELLER_NOT_SUPPORTED";
    public const string BuyerBinInvalid = "INVOICE_BUYER_BIN_INVALID";

    /// <summary>§7: the delivery line keeps the charged amount as its total; <c>net = round_even(amount / (1 + rate))</c>, VAT = the rest.</summary>
    public static (long NetMinor, long VatMinor) SplitDelivery(long amountMinor, decimal vatRate)
    {
        if (amountMinor < 0 || vatRate is < 0m or > 0.5m) throw new ArgumentOutOfRangeException(nameof(amountMinor), "Not a delivery charge or VAT rate.");
        var net = (long)decimal.Round(amountMinor / (1m + vatRate), 0, MidpointRounding.ToEven);
        return (net, amountMinor - net);
    }

    /// <summary>§7: a personal line prints <c>net / quantity</c> to the poisha (2 decimals of a taka), banker's rounding, for display only.</summary>
    public static long DisplayUnitPriceMinor(long netMinor, int quantity) =>
        quantity < 1 ? throw new ArgumentOutOfRangeException(nameof(quantity)) : (long)decimal.Round((decimal)netMinor / quantity, 0, MidpointRounding.ToEven);

    private static InvoiceBuyerSnapshot Buyer(InvoiceIssue issue, InvoiceAddress? shipTo) =>
        BuyerOf(issue.Order, issue.Company, issue.BillingSite, issue.BuyerDisplayName, shipTo);

    /// <summary>
    /// The buyer as printed, read now: the company's legal name, BIN and billing site, or the person's name and the ship-to address. E6.5c
    /// (ADR-0012 §9.4) reads it again for a replacement 6.3 after the company record was fixed; in Live mode it refuses a company without a valid BIN.
    /// </summary>
    public static InvoiceBuyerSnapshot ReplacementBuyer(Order order, Company? company, CompanySite? billingSite, string? buyerDisplayName, InvoicingMode mode)
    {
        ArgumentNullException.ThrowIfNull(order);
        if (mode == InvoicingMode.Live && order.CompanyId is not null && company is not null && (!company.IsActive || !TaxIdFormat.IsBin(Blank(company.Bin))))
            throw new ConflictException(BuyerBinInvalid, "The buyer company must be active with a valid BIN before its replacement invoice is issued.");
        return BuyerOf(order, company, billingSite, buyerDisplayName, ShipTo(order.ShipToJson));
    }

    private static InvoiceBuyerSnapshot BuyerOf(Order order, Company? company, CompanySite? billingSite, string? buyerDisplayName, InvoiceAddress? shipTo)
    {
        if (order.CompanyId is null)
        {
            if (company is not null || billingSite is not null) throw new InvalidOperationException("A personal order has no company.");
            var name = Blank(buyerDisplayName) ?? throw new InvalidOperationException("A personal order needs the buyer's name.");
            return new InvoiceBuyerSnapshot(name, null, Blank(shipTo?.OneLine()));
        }

        if (company is null || company.Id != order.CompanyId || (billingSite is { } s && s.CompanyId != company.Id))
            throw new InvalidOperationException("The company is not the order's.");
        var bin = Blank(company.Bin) is { } raw ? TaxIdFormat.NormalizeBin(raw) ?? raw : null;   // Live: active + valid BIN checked by EnsureLiveIssuable; Specimen prints what is there
        var site = billingSite;
        var address = site is null ? shipTo?.OneLine()
            : string.Join(", ", new[] { site.Line1, site.Line2, site.Upazila, site.District, site.Division, site.PostalCode }.Where(x => !string.IsNullOrWhiteSpace(x)));
        return new InvoiceBuyerSnapshot(company.LegalName, bin, Blank(address));
    }

    private static List<InvoiceLine> Lines(Guid invoiceId, TaxMode taxMode, SubOrder sub, decimal shippingVatRate)
    {
        var lines = new List<InvoiceLine>();
        // Items come from the database in no fixed order; SKU then id makes the line numbers (and so the hash) repeatable.
        foreach (var item in sub.Items.OrderBy(i => i.Sku, StringComparer.Ordinal).ThenBy(i => i.Id))
        {
            var exact = taxMode == TaxMode.Exclusive;
            lines.Add(new InvoiceLine(invoiceId, lines.Count + 1, InvoiceLineKind.Goods, item.Id, item.Sku, item.Title, item.Title, item.Quantity,
                exact ? item.UnitPriceMinor : null, exact ? null : DisplayUnitPriceMinor(item.NetMinor, item.Quantity),
                item.GrossMinor, item.DiscountMinor, item.NetMinor, item.VatRate, item.TaxMinor, item.TotalMinor));
        }

        if (lines.Sum(l => l.NetMinor) != sub.GoodsNetMinor || lines.Sum(l => l.VatMinor) != sub.TaxMinor
            || lines.Sum(l => l.TotalMinor) + sub.ShippingMinor != sub.TotalMinor)
            throw new InvalidOperationException($"Sub-order {sub.SubOrderNumber}: its lines do not add up to its totals; no invoice is issued.");

        if (sub.ShippingMinor > 0)
        {
            var (net, vat) = SplitDelivery(sub.ShippingMinor, shippingVatRate);
            lines.Add(new InvoiceLine(invoiceId, lines.Count + 1, InvoiceLineKind.Delivery, null, null, DeliveryDescriptionEn, DeliveryDescriptionBn, 1,
                net, null, net, 0, net, shippingVatRate, vat, sub.ShippingMinor));
        }

        return lines;
    }

    /// <summary>The display members of the order's ship-to snapshot (E3c shape); anything unreadable is no address.</summary>
    public static InvoiceAddress? ShipTo(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.EnumerateObject().Any()) return null;
            string? Text(string name) => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? Blank(v.GetString()) : null;
            return new InvoiceAddress(Text("siteName"), Text("recipientName"), Text("line1"), Text("line2"), Text("area"), Text("upazila"),
                Text("district"), Text("division"), Text("postalCode"));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
