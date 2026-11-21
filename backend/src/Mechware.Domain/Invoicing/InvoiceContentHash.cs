using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Mechware.Domain.Invoicing;

/// <summary>
/// The content hash of an invoice (ADR-0012 §6, threat model IT6): lower-case hex SHA-256 of a canonical UTF-8 JSON of everything the
/// document prints plus its identity (number, kind, series, fiscal year, serial, issue instant, original invoice) and the
/// consultant-check flag. Fixed property order, no whitespace, money as integer poisha, rates as 4-decimal strings, ids as lower-case
/// "D" GUIDs, enums by name, absent values as null, instants as ISO-8601 UTC with ticks, lines by line number. Not in it: the staff
/// member who issued it, the stored hash itself and the PDF columns. Same canonicaliser pattern as <c>QuoteContentHash</c>.
/// </summary>
public static class InvoiceContentHash
{
    /// <summary>Changing anything in <see cref="CanonicalJson"/> needs a new schema name; issued hashes keep theirs (the reconciliation recomputes each under its own).</summary>
    public const string Schema = "mechware.invoice.v1";

    private static readonly JsonWriterOptions Writer = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Compute(Invoice invoice) => Convert.ToHexStringLower(SHA256.HashData(CanonicalJson(invoice)));

    /// <summary>Whether the stored hash is this content's own (the nightly check, E6.3d, and every PDF render).</summary>
    public static bool Matches(Invoice invoice, string? hash) => hash is not null && string.Equals(hash, Compute(invoice), StringComparison.Ordinal);

    public static byte[] CanonicalJson(Invoice i)
    {
        ArgumentNullException.ThrowIfNull(i);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Writer))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
            w.WriteString("invoiceId", Id(i.Id));
            w.WriteString("kind", i.Kind.ToString());
            w.WriteString("mode", i.Mode.ToString());
            w.WriteString("number", i.Number);
            w.WriteString("series", i.Series.ToString());
            w.WriteNumber("fiscalYear", i.FiscalYear);
            w.WriteNumber("serial", i.Serial);
            w.WriteString("issuedAt", DateTime.SpecifyKind(i.IssuedAtUtc, DateTimeKind.Utc).ToString("O", CultureInfo.InvariantCulture));
            w.WriteString("sellerId", Id(i.SellerId));
            w.WriteString("orderId", Id(i.OrderId));
            w.WriteString("orderNumber", i.OrderNumber);
            w.WriteString("subOrderId", Id(i.SubOrderId));
            Text(w, "shipmentId", Id(i.ShipmentId));
            w.WriteNumber("replacementNo", i.ReplacementNo);
            Text(w, "originalInvoiceId", Id(i.OriginalInvoiceId));
            Text(w, "companyId", Id(i.CompanyId));
            Text(w, "purchaseOrderNumber", i.PurchaseOrderNumber);
            Text(w, "quoteNumber", i.QuoteNumber);
            w.WriteString("currency", i.Currency);
            w.WriteBoolean("consultantChecked", i.ConsultantChecked);

            var s = i.Seller;
            w.WriteStartObject("seller");
            Text(w, "legalNameEn", s.LegalNameEn);
            Text(w, "legalNameBn", s.LegalNameBn);
            Text(w, "bin", s.Bin);
            Text(w, "tin", s.Tin);
            Text(w, "tradeLicence", s.TradeLicence);
            Text(w, "registeredAddressEn", s.RegisteredAddressEn);
            Text(w, "registeredAddressBn", s.RegisteredAddressBn);
            Text(w, "premisesAddressEn", s.PremisesAddressEn);
            Text(w, "premisesAddressBn", s.PremisesAddressBn);
            Text(w, "responsiblePersonName", s.ResponsiblePersonName);
            Text(w, "responsiblePersonDesignation", s.ResponsiblePersonDesignation);
            w.WriteEndObject();

            w.WriteStartObject("buyer");
            w.WriteString("name", i.Buyer.Name);
            Text(w, "bin", i.Buyer.Bin);
            Text(w, "address", i.Buyer.Address);
            w.WriteEndObject();

            w.WriteStartObject("delivery");
            ShipTo(w, i.Delivery.ShipTo);
            w.WriteString("courierName", i.Delivery.CourierName);
            w.WriteString("trackingNumber", i.Delivery.TrackingNumber);
            w.WriteEndObject();

            w.WriteStartArray("lines");
            foreach (var l in i.Lines.OrderBy(l => l.LineNo)) Line(w, l);
            w.WriteEndArray();

            w.WriteStartObject("totals");
            w.WriteNumber("netMinor", i.NetMinor);
            w.WriteNumber("sdMinor", i.SdMinor);
            w.WriteNumber("vatMinor", i.VatMinor);
            w.WriteNumber("totalMinor", i.TotalMinor);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static void Line(Utf8JsonWriter w, InvoiceLine l)
    {
        w.WriteStartObject();
        w.WriteNumber("lineNo", l.LineNo);
        w.WriteString("kind", l.Kind.ToString());
        Text(w, "orderItemId", Id(l.OrderItemId));
        Text(w, "originalLineId", Id(l.OriginalLineId));
        Text(w, "sku", l.Sku);
        Text(w, "partNumber", l.PartNumber);
        w.WriteString("descriptionEn", l.DescriptionEn);
        w.WriteString("descriptionBn", l.DescriptionBn);
        w.WriteString("unit", l.Unit);
        w.WriteNumber("quantity", l.Quantity);
        Number(w, "unitPriceMinor", l.UnitPriceMinor);
        Number(w, "displayUnitPriceMinor", l.DisplayUnitPriceMinor);
        w.WriteNumber("grossMinor", l.GrossMinor);
        w.WriteNumber("discountMinor", l.DiscountMinor);
        w.WriteNumber("netMinor", l.NetMinor);
        w.WriteString("sdRate", Rate(l.SdRate));
        w.WriteNumber("sdMinor", l.SdMinor);
        w.WriteString("vatRate", Rate(l.VatRate));
        w.WriteNumber("vatMinor", l.VatMinor);
        w.WriteNumber("totalMinor", l.TotalMinor);
        w.WriteEndObject();
    }

    private static void ShipTo(Utf8JsonWriter w, InvoiceAddress? a)
    {
        if (a is null)
        {
            w.WriteNull("shipTo");
            return;
        }

        w.WriteStartObject("shipTo");
        Text(w, "siteName", a.SiteName);
        Text(w, "recipientName", a.RecipientName);
        Text(w, "line1", a.Line1);
        Text(w, "line2", a.Line2);
        Text(w, "area", a.Area);
        Text(w, "upazila", a.Upazila);
        Text(w, "district", a.District);
        Text(w, "division", a.Division);
        Text(w, "postalCode", a.PostalCode);
        w.WriteEndObject();
    }

    private static string Rate(decimal rate) => rate.ToString("0.0000", CultureInfo.InvariantCulture);

    private static string Id(Guid id) => id.ToString("D");

    private static string? Id(Guid? id) => id?.ToString("D");

    private static void Text(Utf8JsonWriter w, string name, string? value)
    {
        if (value is null) w.WriteNull(name);
        else w.WriteString(name, value);
    }

    private static void Number(Utf8JsonWriter w, string name, long? value)
    {
        if (value is { } n) w.WriteNumber(name, n);
        else w.WriteNull(name);
    }
}
