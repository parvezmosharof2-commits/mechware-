using Microsoft.EntityFrameworkCore;
using Mechware.Application.Invoicing;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Domain.Pricing;
using Mechware.Domain.Sellers;
using Mechware.Infrastructure.Persistence;

namespace Mechware.IntegrationTests.Invoicing;

/// <summary>
/// Issues test invoices the way E6.3c will, inside the caller's transaction: a shipped order first, the counter last, then the
/// invoice built by the E6.3a builder. Since E6.3b every committed counter step needs its invoice (deferred check), so the E6.2
/// numbering tests issue through here too. Credit notes have no domain yet (E6.5): they are written by SQL from the seller's
/// latest tax invoice of the same mode.
/// </summary>
internal static class InvoiceTestIssues
{
    private const string ShipToJson =
        """{"source":"address","sourceId":null,"districtId":null,"siteName":null,"recipientName":"Nusrat Jahan","phone":"+8801711000000","line1":"House 7, Road 3","line2":null,"upazila":"Gulshan","district":"Dhaka","division":"Dhaka","postalCode":"1212","country":"BD"}""";

    /// <summary>Invoice numbers are unique across sellers, so every test seller prints its own 6-letter prefix.</summary>
    public static string Prefix(Guid sellerId) => string.Concat(sellerId.ToByteArray().Take(6).Select(b => (char)('A' + b % 26)));

    public static InvoicingOptions Options(Guid sellerId, InvoicingMode mode = InvoicingMode.Live, string? prefix = null) => new()
    {
        Mode = mode,
        NumberPrefix = prefix ?? Prefix(sellerId),
        IssuerSellerId = sellerId,
        LegalNameEn = "MECHWARE",
        LegalNameBn = "মেকওয়্যার",
        Bin = "0001234567890",
        RegisteredAddressEn = "Sha-23/E (6th Floor), Dhaka",
        RegisteredAddressBn = "শ-২৩/ই (৬ষ্ঠ তলা), ঢাকা",
        ResponsiblePersonName = "Md. Parvez Mosharaf",
        ResponsiblePersonDesignation = "Managing Director"
    };

    public static async Task<Guid> SellerAsync(MechwareDbContext db)
    {
        var suffix = Guid.NewGuid().ToString("N")[..10];
        var user = User.Create($"invoicing-{suffix}@example.test", null, null, "Invoicing Seller");
        var seller = new Seller(Guid.NewGuid(), user.Id, $"Invoicing Seller {suffix}", $"invoicing-{suffix}", null, null, null, 4.5m, 10, 99m, DateTime.UtcNow);
        db.Users.Add(user);
        db.Sellers.Add(seller);
        await db.SaveChangesAsync();
        return seller.Id;
    }

    public static async Task<Guid> UserAsync(MechwareDbContext db)
    {
        var user = User.Create($"invoicing-{Guid.NewGuid():N}@example.test", null, null, "Invoicing Staff");
        db.Users.Add(user);
        await db.SaveChangesAsync();
        return user.Id;
    }

    public static async Task<Company> ActiveCompanyAsync(MechwareDbContext db)
    {
        var digits = string.Concat(Guid.NewGuid().ToByteArray().Select(b => (char)('0' + b % 10)))[..13];
        var company = Company.Register(Guid.NewGuid(), "Square Textiles Ltd", $"TL-{digits[..8]}", digits, null, null);
        company.Activate();
        db.Companies.Add(company);
        await db.SaveChangesAsync();
        return company;
    }

    /// <summary>A shipped order of the seller (personal, or of <paramref name="company"/> on net basis), placed by the seller's user.</summary>
    public static async Task<(Order Order, Shipment Shipment)> ShippedOrderAsync(MechwareDbContext db, Guid sellerId, Company? company = null, long shippingMinor = 12_000)
    {
        var userId = await db.Sellers.Where(s => s.Id == sellerId).Select(s => s.UserId).SingleAsync();
        var now = DateTime.UtcNow;
        var mode = company is null ? TaxMode.Inclusive : TaxMode.Exclusive;
        var priced = OrderPricing.Price(
            [new(Guid.NewGuid(), Guid.NewGuid(), sellerId, "PLC-1200", "PLC controller", 2, 4_550_000, 0.15m, 100_000),
             new(Guid.NewGuid(), Guid.NewGuid(), sellerId, "CBL-10", "Cable 10 m", 25, 12_345, 0.075m, 0)],
            [new SellerTerms(sellerId, shippingMinor, 0.05m)], mode);
        var order = Order.Place(Guid.NewGuid(), OrderNumber.Generate(now), userId, company?.Id, priced, mode, ShipToJson, OrderStatus.PendingPayment, now,
            paymentDueAtUtc: now.AddMinutes(30), purchaseOrderNumber: company is null ? null : "PO-2026/77");
        order.Confirm(new DeliveryDeadlines(now.AddDays(5), now.AddDays(2), true), null, "paid", now);
        order.StartProcessing(userId, now);
        order.MarkPacked(userId, now);
        var shipment = order.Ship("Steadfast", $"SF-{Guid.NewGuid():N}"[..20], null, userId, now);
        db.Orders.Add(order);
        db.Set<Shipment>().Add(shipment);
        await db.SaveChangesAsync();
        return (order, shipment);
    }

    public static Invoice Build(Order order, Shipment shipment, Guid sellerId, TakenInvoiceNumber taken, Company? company = null, string? prefix = null) =>
        InvoiceSnapshotBuilder.BuildTaxInvoice(
            new InvoiceIssue(Guid.NewGuid(), order, sellerId, shipment, company, null, company is null ? "Nusrat Jahan" : null,
                company is null ? null : "MWQ-2026-000012", taken.Series, taken.FiscalYear, taken.Serial, taken.IssuedAtUtc, order.UserId),
            Options(sellerId, InvoiceNumber.IsSpecimen(taken.Series) ? InvoicingMode.Specimen : InvoicingMode.Live, prefix));

    /// <summary>Everything an issue of <paramref name="series"/> needs before the counter lock; the returned step inserts the invoice of the taken number.</summary>
    public static async Task<Func<TakenInvoiceNumber, Task>> PrepareAsync(MechwareDbContext db, Guid sellerId, InvoiceSeries series)
    {
        if (!InvoiceNumber.IsCreditNote(series))
        {
            var (order, shipment) = await ShippedOrderAsync(db, sellerId);
            return async taken =>
            {
                db.Invoices.Add(Build(order, shipment, sellerId, taken));
                await db.SaveChangesAsync();
            };
        }
        var mode = InvoiceNumber.IsSpecimen(series) ? InvoicingMode.Specimen : InvoicingMode.Live;
        var original = await db.Invoices.AsNoTracking().Where(i => i.SellerId == sellerId && i.Kind == InvoiceKind.TaxInvoice && i.Mode == mode)
            .OrderByDescending(i => i.Serial).Select(i => i.Id).FirstAsync();
        var requester = await UserAsync(db);
        return taken => CreditNoteAsync(db, original, taken, requester);
    }

    public static async Task CreditNoteAsync(MechwareDbContext db, Guid originalId, TakenInvoiceNumber taken, Guid requestedBy, long amountMinor = 100)
    {
        var (id, number, series, at) = (Guid.NewGuid(), taken.Format(Prefix(taken.SellerId)), taken.Series.ToString(), taken.IssuedAtUtc);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO invoices (id, kind, mode, number, seller_id, series, fiscal_year, serial, issued_at, order_id, sub_order_id, replacement_no,
                original_invoice_id, reason, company_id, seller_snapshot, buyer_snapshot, delivery_snapshot, order_number, currency, net_minor, sd_minor,
                vat_minor, total_minor, refund_due_minor, consultant_checked, content_hash, issued_by, requested_by, created_at)
            SELECT {id}, 'CreditNote', o.mode, {number}, o.seller_id, {series}, {taken.FiscalYear}, {taken.Serial}, {at}, o.order_id, o.sub_order_id, 0,
                o.id, 'Return', o.company_id, o.seller_snapshot, o.buyer_snapshot, o.delivery_snapshot, o.order_number, o.currency, {amountMinor}, 0,
                0, {amountMinor}, 0, o.consultant_checked, repeat('c', 64), o.issued_by, {requestedBy}, {at}
              FROM invoices o WHERE o.id = {originalId}
            """);
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO invoice_lines (id, invoice_id, line_no, kind, original_line_id, sku, description_en, description_bn, unit, quantity,
                unit_price_minor, gross_minor, discount_minor, net_minor, sd_rate, sd_minor, vat_rate, vat_minor, total_minor, created_at)
            SELECT {Guid.NewGuid()}, {id}, 1, 'Goods', l.id, l.sku, l.description_en, l.description_bn, l.unit, 1,
                {amountMinor}, {amountMinor}, 0, {amountMinor}, 0, 0, 0, 0, {amountMinor}, {at}
              FROM invoice_lines l WHERE l.invoice_id = {originalId} AND l.line_no = 1
            """);
    }
}
