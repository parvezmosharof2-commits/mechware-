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
    /// <summary>Changing anything in <see cref="CanonicalJson"/> needs a new schema name; issued hashes keep theirs.</summary>
    public const string Schema = "mechware.quote.v1";

    // Hashed only, never rendered or sent: the relaxed encoder keeps Bangla and punctuation literal (fewer escapes to reproduce).
    private static readonly JsonWriterOptions Writer = new() { Indented = false, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Compute(Quote quote, QuoteVersion version) => Convert.ToHexStringLower(SHA256.HashData(CanonicalJson(quote, version)));

    public static byte[] CanonicalJson(Quote quote, QuoteVersion v)
    {
        ArgumentNullException.ThrowIfNull(quote);
        ArgumentNullException.ThrowIfNull(v);
        using var buffer = new MemoryStream();
        using (var w = new Utf8JsonWriter(buffer, Writer))
        {
            w.WriteStartObject();
            w.WriteString("schema", Schema);
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
