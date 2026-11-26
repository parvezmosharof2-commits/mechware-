using System.Reflection;
using System.Runtime.CompilerServices;
using FluentAssertions;
using Mechware.Domain.Common.Exceptions;
using Mechware.Domain.Companies;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;

namespace Mechware.UnitTests.Invoicing;

/// <summary>Shipped orders, companies and options for the invoice tests (E6.3a), built through the real domain paths.</summary>
internal static class InvoiceFixtures
{
    public static readonly DateTime Now = new(2026, 11, 21, 9, 0, 0, DateTimeKind.Utc);
    public static readonly Guid Seller = Guid.Parse("5e11e700-0000-4000-8000-000000000001");
    public static readonly Guid Staff = Guid.Parse("57aff000-0000-4000-8000-000000000002");
    public const string ShipToJson =
        """{"source":"company_site","sourceId":"0b7c6d0e-1111-4000-8000-000000000003","districtId":null,"siteName":"Tongi plant","recipientName":"Rahim Uddin","phone":"+8801711000000","line1":"Plot 12, BSCIC","line2":null,"upazila":"Tongi","district":"Gazipur","division":"Dhaka","postalCode":"1710","country":"BD"}""";

    public static InvoicingOptions Live => new()
    {
        Mode = InvoicingMode.Live,
        IssuerSellerId = Seller,
        LegalNameEn = "MECHWARE",
        LegalNameBn = "মেকওয়্যার",
        Bin = "0001234567890",
        Tin = "123456789012",
        TradeLicence = "TRAD/DSCC/012345/2024",
        RegisteredAddressEn = "Sha-23/E (6th Floor), Dhaka",
        RegisteredAddressBn = "শ-২৩/ই (৬ষ্ঠ তলা), ঢাকা",
        ResponsiblePersonName = "Md. Parvez Mosharaf",
        ResponsiblePersonDesignation = "Managing Director"
    };

    public static Company ActiveCompany(Guid? id = null)
    {
        var c = Company.Register(id ?? Guid.Parse("c0000000-0000-4000-8000-000000000004"), "Square Textiles Ltd", "TL-2020-1234", "0012345678901", null, null);
        c.Activate();
        return c;
    }

    public static CompanySite Site(Company c) => new(Guid.Parse("51700000-0000-4000-8000-000000000005"), c.Id, "Head office", "Karim Ahmed", "+8801811000000",
        "House 7, Road 3", null, "Gulshan", "Dhaka", "Dhaka", "1212", true);

    public static Order Shipped(IReadOnlyList<OrderLineDraft> lines, long shipping, Guid? companyId, out Shipment shipment, string? po = null,
        Guid? orderId = null)
    {
        var mode = companyId is null ? TaxMode.Inclusive : TaxMode.Exclusive;
        var priced = OrderPricing.Price(lines, [new SellerTerms(Seller, shipping, 0.05m)], mode);
        var order = Order.Place(orderId ?? Guid.NewGuid(), "MW-261121-ABCDEF", Guid.Parse("05e70000-0000-4000-8000-000000000006"),
            companyId, priced, mode, ShipToJson, OrderStatus.PendingPayment, Now, paymentDueAtUtc: Now.AddMinutes(30), purchaseOrderNumber: po);
        order.Confirm(new DeliveryDeadlines(Now.AddDays(5), Now.AddDays(2), true), null, "paid", Now);
        order.StartProcessing(Staff, Now);
        order.MarkPacked(Staff, Now);
        shipment = order.Ship("Steadfast", "SF-123", null, Staff, Now);
        return order;
    }

    public static OrderLineDraft Line(string sku, int quantity, long unitPriceMinor, decimal vatRate, long discountMinor = 0) =>
        new(Guid.NewGuid(), Guid.NewGuid(), Seller, sku, $"Item {sku}", quantity, unitPriceMinor, vatRate, discountMinor);

    public static InvoiceIssue Issue(Order order, Shipment shipment, Company? company = null, CompanySite? site = null, InvoiceSeries series = InvoiceSeries.TaxInvoice,
        DateTime? at = null, Guid? invoiceId = null, string? buyerName = null, DateOnly? quoteDate = null) =>
        new(invoiceId ?? Guid.Parse("17000000-0000-4000-8000-000000000007"), order, Seller, shipment, company, site,
            company is null ? buyerName ?? "Nusrat Jahan" : null, company is null ? null : "MWQ-2026-000012", series, FiscalYear.Of(at ?? Now), 482, at ?? Now, Staff, quoteDate);

    /// <summary>A company order (net basis) with two lines, a discount and shipping, issued Live.</summary>
    public static (Invoice Invoice, Order Order, Company Company, CompanySite Site) CompanyInvoice(InvoicingOptions? options = null)
    {
        var company = ActiveCompany();
        var site = Site(company);
        var order = Shipped([Line("PLC-1200", 2, 4_550_000, 0.15m, 100_000), Line("CBL-10", 25, 12_345, 0.075m)], 12_000, company.Id, out var shipment,
            po: "PO-2026/77", orderId: Guid.Parse("0d000000-0000-4000-8000-000000000008"));
        return (InvoiceSnapshotBuilder.BuildTaxInvoice(Issue(order, shipment, company, site), options ?? Live), order, company, site);
    }
}

/// <summary>ADR-0012 §6–§7, threat model §১৪ IT7 and IT8: the snapshot, the line money, the delivery split, the display unit price, Dhaka dates.</summary>
public class InvoiceSnapshotTests
{
    private static readonly decimal[] Rates = [0m, 0.05m, 0.075m, 0.10m, 0.15m];

    /// <summary>IT8 (property): over random orders of both tax modes, every line is the order line's money and the totals equal the order's.</summary>
    [Fact]
    public void InvoiceTotalsEqualTheOrdersToThePoisha()
    {
        var random = new Random(20261121);
        for (var run = 0; run < 400; run++)
        {
            var lines = Enumerable.Range(0, random.Next(1, 7)).Select(n =>
            {
                var qty = random.Next(1, 60);
                var unit = random.NextInt64(1, 2_500_000);
                return InvoiceFixtures.Line($"SKU-{n:D2}", qty, unit, Rates[random.Next(Rates.Length)], random.Next(3) == 0 ? random.NextInt64(0, qty * unit / 3 + 1) : 0);
            }).ToList();
            var company = random.Next(2) == 0 ? InvoiceFixtures.ActiveCompany(Guid.NewGuid()) : null;
            var shipping = random.Next(4) == 0 ? 0 : random.NextInt64(1, 80_000);
            var options = InvoiceFixtures.Live with { ShippingVatRate = Rates[random.Next(Rates.Length)] };
            var order = InvoiceFixtures.Shipped(lines, shipping, company?.Id, out var shipment);
            var invoice = InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment, company), options);

            var sub = order.SubOrders.Single();
            invoice.TotalMinor.Should().Be(order.GrandTotalMinor).And.Be(sub.TotalMinor);
            invoice.NetMinor.Should().Be(invoice.Lines.Sum(l => l.NetMinor));
            invoice.VatMinor.Should().Be(invoice.Lines.Sum(l => l.VatMinor)).And.Be(order.TaxMinor + (invoice.Lines.SingleOrDefault(l => l.Kind == InvoiceLineKind.Delivery)?.VatMinor ?? 0));
            invoice.SdMinor.Should().Be(0);
            invoice.TotalMinor.Should().Be(invoice.NetMinor + invoice.SdMinor + invoice.VatMinor);
            foreach (var item in sub.Items)
            {
                var l = invoice.Lines.Single(x => x.OrderItemId == item.Id);
                (l.Quantity, l.GrossMinor, l.DiscountMinor, l.NetMinor, l.VatRate, l.VatMinor, l.TotalMinor).Should()
                    .Be((item.Quantity, item.GrossMinor, item.DiscountMinor, item.NetMinor, item.VatRate, item.TaxMinor, item.TotalMinor), "VAT stays rounded per line, as the order did");
            }

            invoice.Lines.Count(l => l.Kind == InvoiceLineKind.Delivery).Should().Be(shipping > 0 ? 1 : 0);
            invoice.Lines.Select(l => l.LineNo).Should().Equal(Enumerable.Range(1, invoice.Lines.Count));
            invoice.Lines.Should().OnlyContain(l => l.NetMinor + l.SdMinor + l.VatMinor == l.TotalMinor);
        }
    }

    /// <summary>IT8: the delivery line only splits the charged amount; its total is the sub-order's shipping, whatever the rate.</summary>
    [Fact]
    public void TheDeliveryLineSplit_NeverChangesTheTotal()
    {
        var random = new Random(611);
        for (var i = 0; i < 20_000; i++)
        {
            var amount = random.NextInt64(0, 10_000_000);
            var rate = i % 2 == 0 ? Rates[random.Next(Rates.Length)] : Math.Round((decimal)random.NextDouble() / 2, 4);
            var (net, vat) = InvoiceSnapshotBuilder.SplitDelivery(amount, rate);
            (net + vat).Should().Be(amount);
            net.Should().Be((long)decimal.Round(amount / (1m + rate), 0, MidpointRounding.ToEven));
            vat.Should().BeGreaterThanOrEqualTo(0);
        }

        InvoiceSnapshotBuilder.SplitDelivery(12_000, 0.15m).Should().Be((10_435L, 1_565L));
        InvoiceSnapshotBuilder.SplitDelivery(12_000, 0m).Should().Be((12_000L, 0L));
        var (invoice, order, _, _) = InvoiceFixtures.CompanyInvoice(InvoiceFixtures.Live with { ShippingVatRate = 0.15m });
        var delivery = invoice.Lines.Single(l => l.Kind == InvoiceLineKind.Delivery);
        (delivery.LineNo, delivery.Quantity, delivery.UnitPriceMinor, delivery.NetMinor, delivery.VatMinor, delivery.TotalMinor, delivery.VatRate)
            .Should().Be((3, 1, 10_435L, 10_435L, 1_565L, 12_000L, 0.15m));
        (delivery.DescriptionEn, delivery.DescriptionBn, delivery.OrderItemId, delivery.Sku).Should().Be(("Delivery charge", "ডেলিভারি চার্জ", (Guid?)null, (string?)null));
        invoice.TotalMinor.Should().Be(order.GrandTotalMinor);
    }

    /// <summary>IT8: a personal (VAT-inclusive) line prints net / quantity rounded for display; its values stay the order's exact ones.</summary>
    [Fact]
    public void PersonalOrderUnitPrices_AreDisplayOnly_LineValuesAreExact()
    {
        // 3 × 100.00 incl. 15 %: net 260.87 (26 087 poisha); 26 087 / 3 = 8 695.67 → 86.96 per unit, but 3 × 86.96 = 260.88.
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("SNS-1", 3, 10_000, 0.15m)], 0, null, out var shipment);
        var line = InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment), InvoiceFixtures.Live).Lines.Single();
        (line.UnitPriceMinor, line.DisplayUnitPriceMinor).Should().Be(((long?)null, (long?)8_696));
        (line.GrossMinor, line.NetMinor, line.VatMinor, line.TotalMinor).Should().Be((30_000L, 26_087L, 3_913L, 30_000L));

        InvoiceSnapshotBuilder.DisplayUnitPriceMinor(25, 2).Should().Be(12, "12.5 rounds to even");
        InvoiceSnapshotBuilder.DisplayUnitPriceMinor(35, 2).Should().Be(18, "17.5 rounds to even");

        var company = InvoiceFixtures.CompanyInvoice().Invoice.Lines.First(l => l.Kind == InvoiceLineKind.Goods);
        (company.UnitPriceMinor, company.DisplayUnitPriceMinor).Should().Be(((long?)12_345, (long?)null), "a net-basis unit price is printed exactly");
    }

    [Fact]
    public void TheSnapshot_CopiesSellerBuyerDeliveryAndReferences()
    {
        var (invoice, order, company, _) = InvoiceFixtures.CompanyInvoice();
        invoice.Number.Should().Be("MW-INV-2026-27-000482");
        (invoice.Kind, invoice.Mode, invoice.Series, invoice.FiscalYear, invoice.Serial).Should().Be((InvoiceKind.TaxInvoice, InvoicingMode.Live, InvoiceSeries.TaxInvoice, 2026, 482L));
        invoice.Seller.Should().Be(new InvoiceSellerSnapshot("MECHWARE", "মেকওয়্যার", "0001234567890", "123456789012", "TRAD/DSCC/012345/2024",
            "Sha-23/E (6th Floor), Dhaka", "শ-২৩/ই (৬ষ্ঠ তলা), ঢাকা", "Sha-23/E (6th Floor), Dhaka", "শ-২৩/ই (৬ষ্ঠ তলা), ঢাকা", "Md. Parvez Mosharaf", "Managing Director"));
        invoice.Buyer.Should().Be(new InvoiceBuyerSnapshot("Square Textiles Ltd", "0012345678901", "House 7, Road 3, Gulshan, Dhaka, Dhaka, 1212"));
        invoice.Delivery.Should().Be(new InvoiceDeliverySnapshot(
            new InvoiceAddress("Tongi plant", "Rahim Uddin", "Plot 12, BSCIC", null, null, "Tongi", "Gazipur", "Dhaka", "1710"), "Steadfast", "SF-123"));
        (invoice.OrderNumber, invoice.PurchaseOrderNumber, invoice.QuoteNumber, invoice.CompanyId, invoice.OrderId, invoice.SubOrderId)
            .Should().Be((order.OrderNumber, "PO-2026/77", "MWQ-2026-000012", (Guid?)company.Id, order.Id, order.SubOrders.Single().Id));
        invoice.ConsultantChecked.Should().BeFalse();
        invoice.Lines.Select(l => l.Sku).Should().Equal("CBL-10", "PLC-1200", null);

        var personal = InvoiceFixtures.Shipped([InvoiceFixtures.Line("A", 1, 500, 0.15m)], 0, null, out var shipment);
        var p = InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(personal, shipment, buyerName: "  Nusrat Jahan "), InvoiceFixtures.Live);
        p.Buyer.Should().Be(new InvoiceBuyerSnapshot("Nusrat Jahan", null, "Plot 12, BSCIC, Tongi, Gazipur, Dhaka, 1710"), "a person has no BIN (unregistered)");
        (p.CompanyId, p.QuoteNumber, p.PurchaseOrderNumber).Should().Be(((Guid?)null, (string?)null, (string?)null));
    }

    /// <summary>IT7: a later change of the configuration never reaches an issued invoice; its stored hash still verifies.</summary>
    [Fact]
    public void ChangingTheIssuerConfig_DoesNotRewriteOldInvoices()
    {
        var (old, _, _, _) = InvoiceFixtures.CompanyInvoice();
        var seller = old.Seller;
        var changed = InvoiceFixtures.Live with { LegalNameEn = "MECHWARE Ltd", Bin = "0009999999990", RegisteredAddressEn = "New office", ConsultantChecked = true };
        var (later, _, _, _) = InvoiceFixtures.CompanyInvoice(changed);

        old.Seller.Should().BeSameAs(seller).And.Be(InvoiceFixtures.CompanyInvoice().Invoice.Seller);
        old.ConsultantChecked.Should().BeFalse();
        InvoiceContentHash.Matches(old, old.ContentHash).Should().BeTrue();
        (later.Seller.LegalNameEn, later.Seller.Bin, later.Seller.RegisteredAddressEn, later.ConsultantChecked).Should().Be(("MECHWARE Ltd", "0009999999990", "New office", true));
        later.ContentHash.Should().NotBe(old.ContentHash);
    }

    /// <summary>IT7: the buyer's billing address is copied, so editing the company site later changes nothing on the invoice.</summary>
    [Fact]
    public void ChangingTheCompanySiteAfterIssue_DoesNotRewriteTheInvoice()
    {
        var (invoice, _, _, site) = InvoiceFixtures.CompanyInvoice();
        var hash = invoice.ContentHash;
        site.Update("Moved", "Someone Else", "+8801900000000", "Somewhere new", "Floor 9", "Mirpur", "Dhaka", "Dhaka", "1216");
        invoice.Buyer.Address.Should().Be("House 7, Road 3, Gulshan, Dhaka, Dhaka, 1212");
        InvoiceContentHash.Compute(invoice).Should().Be(hash);
    }

    [Fact]
    public void AnInvoice_HasNoPublicSetters_AndItsLinesCannotBeChanged()
    {
        Type[] types = [typeof(Invoice), typeof(InvoiceLine), typeof(InvoiceSellerSnapshot), typeof(InvoiceBuyerSnapshot), typeof(InvoiceDeliverySnapshot), typeof(InvoiceAddress)];
        var writable = types.SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            .Where(p => p.SetMethod is { IsPublic: true } set && !set.ReturnParameter.GetRequiredCustomModifiers().Contains(typeof(IsExternalInit)))
            .Select(p => $"{p.DeclaringType!.Name}.{p.Name}");
        writable.Should().BeEmpty("an issued invoice is never changed (I2)");

        var invoice = InvoiceFixtures.CompanyInvoice().Invoice;
        var add = () => ((ICollection<InvoiceLine>)invoice.Lines).Add(invoice.Lines[0]);
        add.Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void LiveMode_RefusesIncompleteSetup_AnotherSeller_AndABuyerWithoutAValidBin()
    {
        var company = InvoiceFixtures.ActiveCompany();
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("A", 1, 500, 0.15m)], 0, company.Id, out var shipment);
        var issue = InvoiceFixtures.Issue(order, shipment, company);

        Code(() => InvoiceSnapshotBuilder.BuildTaxInvoice(issue, InvoiceFixtures.Live with { Bin = null })).Should().Be("INVOICE_SETUP_INCOMPLETE");
        Code(() => InvoiceSnapshotBuilder.BuildTaxInvoice(issue, InvoiceFixtures.Live with { IssuerSellerId = Guid.NewGuid() })).Should().Be("INVOICE_SELLER_NOT_SUPPORTED");
        company.Suspend();
        Code(() => InvoiceSnapshotBuilder.BuildTaxInvoice(issue, InvoiceFixtures.Live)).Should().Be("INVOICE_BUYER_BIN_INVALID");

        // A specimen prints what there is: an inactive company and missing facts are fine, the number is SPEC-.
        var specimen = InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { Series = InvoiceSeries.SpecimenTaxInvoice }, new InvoicingOptions { Mode = InvoicingMode.Specimen });
        (specimen.Number, specimen.Mode, specimen.Seller.LegalNameEn, specimen.Seller.Bin).Should().Be(("SPEC-INV-2026-27-000482", InvoicingMode.Specimen, (string?)null, (string?)null));
    }

    [Fact]
    public void TheBuilder_RefusesInputsThatDoNotBelongTogether()
    {
        var company = InvoiceFixtures.ActiveCompany();
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("A", 1, 500, 0.15m)], 0, company.Id, out var shipment);
        var issue = InvoiceFixtures.Issue(order, shipment, company);
        var live = InvoiceFixtures.Live;
        _ = InvoiceFixtures.Shipped([InvoiceFixtures.Line("A", 1, 500, 0.15m)], 0, company.Id, out var otherShipment);

        Action[] refused =
        [
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue, live with { Mode = InvoicingMode.Off }),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue, live with { Mode = null }),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { Series = InvoiceSeries.SpecimenTaxInvoice }, live),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { Series = InvoiceSeries.CreditNote }, live),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { FiscalYear = 2025 }, live),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { Serial = 0 }, live),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { IssuedAtUtc = DateTime.SpecifyKind(InvoiceFixtures.Now, DateTimeKind.Unspecified) }, live),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { Shipment = otherShipment }, live),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { SellerId = Guid.NewGuid(), Series = InvoiceSeries.SpecimenTaxInvoice }, live with { Mode = InvoicingMode.Specimen }),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { Company = InvoiceFixtures.ActiveCompany(Guid.NewGuid()) }, live),
            () => InvoiceSnapshotBuilder.BuildTaxInvoice(issue with { Company = null }, live),
        ];
        foreach (var act in refused) act.Should().Throw<InvalidOperationException>();

        var notShipped = InvoiceFixtures.Shipped([InvoiceFixtures.Line("A", 1, 500, 0.15m)], 0, null, out var s2);
        notShipped.MarkDelivered(s2, InvoiceFixtures.Staff, InvoiceFixtures.Now);
        FluentActions.Invoking(() => InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(notShipped, s2), live)).Should().Throw<InvalidOperationException>();
        var personal = InvoiceFixtures.Shipped([InvoiceFixtures.Line("A", 1, 500, 0.15m)], 0, null, out var s3);
        FluentActions.Invoking(() => InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(personal, s3, buyerName: " "), live))
            .Should().Throw<InvalidOperationException>("a personal invoice names its buyer");
        FluentActions.Invoking(() => InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(personal, s3) with { Company = company }, live))
            .Should().Throw<InvalidOperationException>("a personal order has no company");
    }

    /// <summary>ADR-0012 §5, IT10: the printed time, the fiscal year in the number and the 9.1 month all follow the Dhaka calendar.</summary>
    [Theory]
    [InlineData("2026-06-30T17:59:59", "30-06-2026 23:59", "MW-INV-2025-26-000482", 2026, 6)]
    [InlineData("2026-06-30T18:00:00", "01-07-2026 00:00", "MW-INV-2026-27-000482", 2026, 7)]
    [InlineData("2026-12-31T18:30:00", "01-01-2027 00:30", "MW-INV-2026-27-000482", 2027, 1)]
    [InlineData("2026-11-30T17:59:00", "30-11-2026 23:59", "MW-INV-2026-27-000482", 2026, 11)]
    public void TheInvoiceDates_AreDhakaDates(string utc, string printed, string number, int returnYear, int returnMonth)
    {
        var at = DateTime.SpecifyKind(DateTime.Parse(utc, System.Globalization.CultureInfo.InvariantCulture), DateTimeKind.Utc);
        var company = InvoiceFixtures.ActiveCompany();
        var order = InvoiceFixtures.Shipped([InvoiceFixtures.Line("A", 1, 500, 0.15m)], 0, company.Id, out var shipment);
        var invoice = InvoiceSnapshotBuilder.BuildTaxInvoice(InvoiceFixtures.Issue(order, shipment, company, at: at), InvoiceFixtures.Live);
        (invoice.PrintedIssueTime, invoice.Number, invoice.IssuedAtUtc).Should().Be((printed, number, at));
        InvoiceDates.ReturnMonth(at).Should().Be((returnYear, returnMonth));
    }

    private static string? Code(Action act)
    {
        try
        {
            act();
            return null;
        }
        catch (ConflictException e)
        {
            return e.Code;
        }
    }
}
