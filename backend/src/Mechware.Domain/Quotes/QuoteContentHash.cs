using System.Globalization;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using Mechware.Domain.Common.Enums;

namespace Mechware.Domain.Quotes;

/// <summary>
/// The content hash of a version (ADR-0011 §6, Q1): lower-case hex SHA-256 of a canonical UTF-8 JSON of everything the buyer
/// sees and accepts. Fixed property order, no whitespace, money as integers, VAT rates as 4-decimal strings, ids as lower-case
/// "D" GUIDs, enums by name, absent values as null, lines by line number and fees by seller. Not in it: statuses, timestamps,
/// staff ids, the preparer, the internal note, below-floor reasons, check figures and flags. The domain computes it at the send
/// and recomputes it on every approval and acceptance, so a caller never supplies it and a changed row is caught.
/// </summary>
public static class QuoteContentHash
{
    /// <summary>
    /// Changing anything in <see cref="CanonicalJson"/> needs a new schema name; issued hashes keep theirs. v2 (D4.7) adds the ship-to
    /// address snapshot (<c>shipping.shipTo</c>); v1 had only the site id.
    /// </summary>
    public const string Schema = "mechware.quote.v2";

    /// <summary>The first schema: without <c>shipping.shipTo</c>. A v1 hash still verifies for a version that has no ship-to snapshot.</summary>
    public const string SchemaV1 = "mechware.quote.v1";

    // Hashed only, never rendered or sent: the relaxed encoder keeps Bangla and punctuation literal (fewer escapes to reproduce).
    private static readonly JsonWriterOptions Writer = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The hash of the current schema; every new send stores this one.</summary>
    public static string Compute(Quote quote, QuoteVersion version) => Hex(CanonicalJson(quote, version));

    /// <summary>
    /// Whether <paramref name="hash"/> is this content's own: the current schema, or v1 for a version without a ship-to snapshot (v1
    /// had no field for it, so it covers exactly the same content there). A version with a snapshot verifies only under v2.
    /// </summary>
    public static bool Matches(Quote quote, QuoteVersion version, string? hash)
    {
        ArgumentNullException.ThrowIfNull(version);
        return hash is not null && (string.Equals(hash, Compute(quote, version), StringComparison.Ordinal)
            || (version.ShipTo is null && string.Equals(hash, Hex(CanonicalJson(quote, version, SchemaV1)), StringComparison.Ordinal)));
    }

    private static string Hex(byte[] json) => Convert.ToHexStringLower(SHA256.HashData(json));

    public static byte[] CanonicalJson(Quote quote, QuoteVersion v, string schema = Schema)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(v);
        if (schema is not (Schema or SchemaV1)) throw new ArgumentOutOfRangeException(nameof(schema), schema, "Unknown quote hash schema.");
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Writer))
        {
            w.WriteStartObject();
            w.WriteString("schema", schema);
            w.WriteString("quoteId", Id(quote.Id));
            w.WriteString("number", quote.Number);
            w.WriteNumber("versionNo", v.VersionNo);
            w.WriteString("currency", Currency.BDT.ToIsoCode());
            w.WriteString("companyId", Id(quote.CompanyId));
            w.WriteStartObject("company");
            Text(w, "name", v.CompanySnapshot?.Name);
            Text(w, "bin", v.CompanySnapshot?.Bin);
            Text(w, "billingAddress", v.CompanySnapshot?.BillingAddress);
            w.WriteEndObject();
            w.WriteString("contactUserId", Id(quote.ContactUserId));
            w.WriteString("paymentTerms", v.PaymentTerms.ToString());
            w.WriteNumber("validityDays", v.ValidityDays);
            Text(w, "termsText", v.TermsText);
            Text(w, "buyerNote", v.BuyerNote);

            w.WriteStartObject("shipping");
            w.WriteString("mode", v.ShippingMode.ToString());
            Text(w, "shipToSiteId", v.ShipToSiteId is { } site ? Id(site) : null);
            if (schema != SchemaV1) ShipTo(w, v.ShipTo);
            w.WriteStartArray("fees");
            foreach (var f in (v.ShippingFees ?? []).OrderBy(f => Id(f.SellerId), StringComparer.Ordinal))
            {
                w.WriteStartObject();
                w.WriteString("sellerId", Id(f.SellerId));
                w.WriteNumber("shippingMinor", f.ShippingMinor);
                w.WriteEndObject();
            }

            w.WriteEndArray();
            w.WriteEndObject();

            w.WriteStartArray("lines");
            foreach (var l in v.Lines.OrderBy(l => l.LineNo))
            {
                w.WriteStartObject();
                w.WriteNumber("lineNo", l.LineNo);
                w.WriteString("variantId", Id(l.VariantId));
                w.WriteString("sellerId", Id(l.SellerId));
                w.WriteString("sku", l.Sku);
                Text(w, "partNumber", l.PartNumber);
                w.WriteString("titleEn", l.TitleEn);
                w.WriteString("titleBn", l.TitleBn);
                w.WriteNumber("quantity", l.Quantity);
                w.WriteNumber("unitPriceMinor", l.UnitPriceMinor);
                w.WriteString("vatRate", l.VatRate.ToString("0.0000", CultureInfo.InvariantCulture));
                w.WriteNumber("lineNetMinor", l.LineNetMinor);
                w.WriteNumber("lineTaxMinor", l.LineTaxMinor);
                w.WriteNumber("lineTotalMinor", l.LineTotalMinor);
                w.WriteNumber("publicUnitPriceMinor", l.PublicUnitPriceMinor);
                Text(w, "lineNote", l.LineNote);
                w.WriteEndObject();
            }

            w.WriteEndArray();

            w.WriteStartObject("totals");
            w.WriteNumber("goodsNetMinor", v.GoodsNetMinor);
            w.WriteNumber("taxMinor", v.TaxMinor);
            w.WriteNumber("goodsTotalMinor", v.GoodsTotalMinor);
            Number(w, "shippingTotalMinor", v.ShippingTotalMinor);
            Number(w, "grandTotalMinor", v.GrandTotalMinor);
            w.WriteEndObject();
            w.WriteEndObject();
        }

        return buffer.ToArray();
    }

    private static string Id(Guid id) => id.ToString("D");

    private static void ShipTo(Utf8JsonWriter w, QuoteShipTo? a)
    {
        if (a is null)
        {
            w.WriteNull("shipTo");
            return;
        }

        w.WriteStartObject("shipTo");
        w.WriteString("siteName", a.SiteName);
        w.WriteString("recipientName", a.RecipientName);
        w.WriteString("phone", a.Phone);
        w.WriteString("line1", a.Line1);
        Text(w, "line2", a.Line2);
        w.WriteString("upazila", a.Upazila);
        w.WriteString("district", a.District);
        w.WriteString("division", a.Division);
        Text(w, "postalCode", a.PostalCode);
        w.WriteEndObject();
    }

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
