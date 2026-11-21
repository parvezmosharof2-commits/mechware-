using System.Collections;
using System.Reflection;
using System.Text;
using FluentAssertions;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;

namespace Mechware.UnitTests.Invoicing;

/// <summary>ADR-0012 §6, threat model §১৪ IT6: the canonical content hash (schema <c>mechware.invoice.v1</c>).</summary>
public class InvoiceContentHashTests
{
    private const BindingFlags Instance = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

    /// <summary>Not printed and not part of the document's identity (row ids, timestamps, who pressed ship, the hash itself); everything else is hashed.</summary>
    private static readonly string[] NotHashed =
    [
        "Invoice.ContentHash", "Invoice.IssuedBy", "Invoice.CreatedAtUtc", "Invoice.UpdatedAtUtc",
        "InvoiceLine.Id", "InvoiceLine.InvoiceId", "InvoiceLine.CreatedAtUtc", "InvoiceLine.UpdatedAtUtc"
    ];

    /// <summary>The ids the domain draws at random (sub-order, items, shipment) pinned, so the hash below is the same on every run.</summary>
    private static Invoice Pinned()
    {
        var invoice = InvoiceFixtures.CompanyInvoice(InvoiceFixtures.Live with { ShippingVatRate = 0.15m }).Invoice;
        Set(invoice, nameof(Invoice.SubOrderId), Guid.Parse("50b00000-0000-4000-8000-000000000009"));
        Set(invoice, nameof(Invoice.ShipmentId), (Guid?)Guid.Parse("5b100000-0000-4000-8000-00000000000a"));
        foreach (var line in invoice.Lines.Where(l => l.OrderItemId is not null))
            Set(line, nameof(InvoiceLine.OrderItemId), (Guid?)Guid.Parse($"17e70000-0000-4000-8000-{line.LineNo:D12}"));
        return invoice;
    }

    [Fact]
    public void TheContentHash_IsAGoldenValue_OfTheCanonicalJson()
    {
        var invoice = Pinned();
        var json = Encoding.UTF8.GetString(InvoiceContentHash.CanonicalJson(invoice));
        json.Should().Be(
            """{"schema":"mechware.invoice.v1","invoiceId":"17000000-0000-4000-8000-000000000007","kind":"TaxInvoice","mode":"Live","number":"MW-INV-2026-27-000482","series":"TaxInvoice","fiscalYear":2026,"serial":482,"issuedAt":"2026-11-21T09:00:00.0000000Z","sellerId":"5e11e700-0000-4000-8000-000000000001","orderId":"0d000000-0000-4000-8000-000000000008","orderNumber":"MW-261121-ABCDEF","subOrderId":"50b00000-0000-4000-8000-000000000009","shipmentId":"5b100000-0000-4000-8000-00000000000a","replacementNo":0,"originalInvoiceId":null,"companyId":"c0000000-0000-4000-8000-000000000004","purchaseOrderNumber":"PO-2026/77","quoteNumber":"MWQ-2026-000012","currency":"BDT","consultantChecked":false,"seller":{"legalNameEn":"MECHWARE","legalNameBn":"মেকওয়্যার","bin":"0001234567890","tin":"123456789012","tradeLicence":"TRAD/DSCC/012345/2024","registeredAddressEn":"Sha-23/E (6th Floor), Dhaka","registeredAddressBn":"শ-২৩/ই (৬ষ্ঠ তলা), ঢাকা","premisesAddressEn":"Sha-23/E (6th Floor), Dhaka","premisesAddressBn":"শ-২৩/ই (৬ষ্ঠ তলা), ঢাকা","responsiblePersonName":"Md. Parvez Mosharaf","responsiblePersonDesignation":"Managing Director"},"buyer":{"name":"Square Textiles Ltd","bin":"0012345678901","address":"House 7, Road 3, Gulshan, Dhaka, Dhaka, 1212"},"delivery":{"shipTo":{"siteName":"Tongi plant","recipientName":"Rahim Uddin","line1":"Plot 12, BSCIC","line2":null,"area":null,"upazila":"Tongi","district":"Gazipur","division":"Dhaka","postalCode":"1710"},"courierName":"Steadfast","trackingNumber":"SF-123"},"lines":[{"lineNo":1,"kind":"Goods","orderItemId":"17e70000-0000-4000-8000-000000000001","originalLineId":null,"sku":"CBL-10","partNumber":null,"descriptionEn":"Item CBL-10","descriptionBn":"Item CBL-10","unit":"pcs","quantity":25,"unitPriceMinor":12345,"displayUnitPriceMinor":null,"grossMinor":308625,"discountMinor":0,"netMinor":308625,"sdRate":"0.0000","sdMinor":0,"vatRate":"0.0750","vatMinor":23147,"totalMinor":331772},{"lineNo":2,"kind":"Goods","orderItemId":"17e70000-0000-4000-8000-000000000002","originalLineId":null,"sku":"PLC-1200","partNumber":null,"descriptionEn":"Item PLC-1200","descriptionBn":"Item PLC-1200","unit":"pcs","quantity":2,"unitPriceMinor":4550000,"displayUnitPriceMinor":null,"grossMinor":9100000,"discountMinor":100000,"netMinor":9000000,"sdRate":"0.0000","sdMinor":0,"vatRate":"0.1500","vatMinor":1350000,"totalMinor":10350000},{"lineNo":3,"kind":"Delivery","orderItemId":null,"originalLineId":null,"sku":null,"partNumber":null,"descriptionEn":"Delivery charge","descriptionBn":"ডেলিভারি চার্জ","unit":"pcs","quantity":1,"unitPriceMinor":10435,"displayUnitPriceMinor":null,"grossMinor":10435,"discountMinor":0,"netMinor":10435,"sdRate":"0.0000","sdMinor":0,"vatRate":"0.1500","vatMinor":1565,"totalMinor":12000}],"totals":{"netMinor":9319060,"sdMinor":0,"vatMinor":1374712,"totalMinor":10693772}}""");
        InvoiceContentHash.Compute(invoice).Should().Be(GoldenHash, "an issued hash must recompute the same forever; a change needs schema v2");
        invoice.ContentHash.Should().MatchRegex("^[0-9a-f]{64}$");
    }

    // SHA-256 of the UTF-8 JSON above (checked independently with Python's hashlib). If this fails while the JSON passes, the hashing changed.
    private const string GoldenHash = "ed814e08b30c416722fb2d5c293b5a7c7b62de6ecda79f92575226a381bd8143";

    /// <summary>IT6 (property): changing any single stored field of the invoice, its snapshots or any line changes the hash.</summary>
    [Fact]
    public void TheContentHash_ChangesWithEveryPrintedField()
    {
        var invoice = InvoiceFixtures.CompanyInvoice().Invoice;
        var baseline = InvoiceContentHash.Compute(invoice);
        var tried = new List<string>();
        IEnumerable<object> targets = [invoice, .. invoice.Lines];
        foreach (var target in targets)
            foreach (var p in Settable(target.GetType()))
                foreach (var (label, variant) in Variants(p.GetValue(target), p.PropertyType, $"{target.GetType().Name}.{p.Name}"))
                {
                    var old = p.GetValue(target);
                    p.SetValue(target, variant);
                    var hash = InvoiceContentHash.Compute(invoice);
                    p.SetValue(target, old);
                    hash.Should().NotBe(baseline, $"{label} is printed or identifies the invoice");
                    tried.Add(label);
                }

        InvoiceContentHash.Compute(invoice).Should().Be(baseline);
        tried.Should().Contain(["Invoice.Buyer.Bin", "Invoice.Seller.Bin", "Invoice.Delivery.ShipTo.Line1", "InvoiceLine.DiscountMinor", "Invoice.ConsultantChecked"]);
        tried.Count.Should().BeGreaterThan(80);
    }

    /// <summary>Intended independence: the order the items come from the database in, and the order the lines are held in, change nothing.</summary>
    [Fact]
    public void TheContentHash_DoesNotDependOnTheOrderOfItemsOrLines()
    {
        var company = InvoiceFixtures.ActiveCompany();
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("B", 1, 700, 0.15m), InvoiceFixtures.Line("A", 2, 500, 0.05m), InvoiceFixtures.Line("C", 3, 900, 0m)],
            5_000, company.Id, out var shipment);
        var issue = InvoiceFixtures.Issue(order, shipment, company);
        var first = InvoiceSnapshotBuilder.BuildTaxInvoice(issue, InvoiceFixtures.Live);

        var items = (IList)typeof(SubOrder).GetField("_items", Instance)!.GetValue(order.SubOrders.Single())!;
        var reversed = items.Cast<object>().Reverse().ToList();
        items.Clear();
        foreach (var i in reversed) items.Add(i);
        var second = InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { IssuedBy = Guid.NewGuid() }, InvoiceFixtures.Live);

        second.ContentHash.Should().Be(first.ContentHash, "line numbers follow SKU, and the issuing staff member is not printed");
        second.Lines.Select(l => l.Sku).Should().Equal("A", "B", "C", null);

        var lines = (IList)typeof(Invoice).GetField("_lines", Instance)!.GetValue(second)!;
        var held = lines.Cast<object>().Reverse().ToList();
        lines.Clear();
        foreach (var l in held) lines.Add(l);
        InvoiceContentHash.Compute(second).Should().Be(first.ContentHash);
    }

    [Fact]
    public void Matches_IsFalse_ForAnotherHash_OrAChangedInvoice()
    {
        var invoice = InvoiceFixtures.CompanyInvoice().Invoice;
        InvoiceContentHash.Matches(invoice, invoice.ContentHash).Should().BeTrue();
        InvoiceContentHash.Matches(invoice, null).Should().BeFalse();
        InvoiceContentHash.Matches(invoice, invoice.ContentHash.ToUpperInvariant()).Should().BeFalse();
        Set(invoice.Lines[0], nameof(InvoiceLine.NetMinor), invoice.Lines[0].NetMinor - 1);
        InvoiceContentHash.Matches(invoice, invoice.ContentHash).Should().BeFalse();
    }

    private static IEnumerable<PropertyInfo> Settable(Type t) =>
        t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.GetSetMethod(true) is not null && !NotHashed.Contains($"{t.Name}.{p.Name}"));

    /// <summary>One changed value per scalar; a snapshot record yields one copy per member changed (records are cloned, never edited).</summary>
    private static IEnumerable<(string Label, object? Value)> Variants(object? value, Type type, string label)
    {
        var t = Nullable.GetUnderlyingType(type) ?? type;
        if (t == typeof(string)) yield return (label, (string?)value + "x");
        else if (t == typeof(Guid)) yield return (label, Guid.NewGuid());
        else if (t == typeof(int)) yield return (label, ((int?)value ?? 0) + 1);
        else if (t == typeof(long)) yield return (label, ((long?)value ?? 0) + 1);
        else if (t == typeof(decimal)) yield return (label, (decimal)value! + 0.0001m);
        else if (t == typeof(bool)) yield return (label, !(bool)value!);
        else if (t == typeof(DateTime)) yield return (label, ((DateTime)value!).AddTicks(1));
        else if (t.IsEnum)
        {
            var values = Enum.GetValues(t);
            yield return (label, values.GetValue((Array.IndexOf(values, value) + 1) % values.Length));
        }
        else if (t.GetMethod("<Clone>$") is { } clone)
        {
            value.Should().NotBeNull($"the fixture fills {label}");
            foreach (var member in t.GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(m => m.SetMethod is not null))
                foreach (var (inner, changed) in Variants(member.GetValue(value), member.PropertyType, $"{label}.{member.Name}"))
                {
                    var copy = clone.Invoke(value, null)!;
                    member.SetValue(copy, changed);
                    yield return (inner, copy);
                }
        }
        else throw new InvalidOperationException($"{label}: no variant for {t.Name}; teach the test this type.");
    }

    private static void Set(object target, string name, object? value) => target.GetType().GetProperty(name)!.SetValue(target, value);
}
