using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Orders;
using Mechware.Domain.Sellers;
using Mechware.Infrastructure.Security;

namespace Mechware.Infrastructure.Persistence.Configurations;

/// <summary>
/// E6.2 numbering counters (ADR-0012 §4, §16): one row per (seller, series), no incoming foreign key (so <c>FOR UPDATE</c> is
/// safe, §15). The CHECKs repeat the row's two shapes; <see cref="InvoiceNumberingSql"/> adds the step guard. Written only by
/// raw SQL in <c>InvoiceNumberAllocator</c>, never through the change tracker.
/// </summary>
internal sealed class InvoiceNumberCounterConfiguration : IEntityTypeConfiguration<InvoiceNumberCounter>
{
    public void Configure(EntityTypeBuilder<InvoiceNumberCounter> b)
    {
        b.ToTable("invoice_number_counters", t =>
        {
            t.HasCheckConstraint("ck_invoice_number_counters_series",
                "series IN ('TaxInvoice', 'CreditNote', 'SpecimenTaxInvoice', 'SpecimenCreditNote')");
            t.HasCheckConstraint("ck_invoice_number_counters_state",
                $"(fiscal_year = 0 AND next_serial = 1 AND last_issued_at IS NULL) OR (fiscal_year BETWEEN {FiscalYear.Min} AND {FiscalYear.Max} AND next_serial >= 2 AND last_issued_at IS NOT NULL)");
        });
        b.HasKey(c => new { c.SellerId, c.Series });
        b.Property(c => c.SellerId).HasColumnName("seller_id");
        b.Property(c => c.Series).HasColumnName("series").HasConversion<string>().HasMaxLength(24);
        b.Property(c => c.Year).HasColumnName("fiscal_year");
        b.Property(c => c.NextSerial).HasColumnName("next_serial");
        b.Property(c => c.LastIssuedAtUtc).HasColumnName("last_issued_at");
        b.HasOne<Seller>().WithMany().HasForeignKey(c => c.SellerId).OnDelete(DeleteBehavior.Restrict);
    }
}

/// <summary>
/// E6.3b issued invoices, both kinds (ADR-0012 §8, §16). Money is bigint poisha like <c>orders</c> and the domain (never numeric
/// taka), enums are text with CHECK lists, every FK is RESTRICT, nothing is ever deleted. The CHECKs repeat the row shapes; what
/// needs the previous row, another table or the end of the transaction lives in <see cref="InvoiceSql"/>. Columns the E6.3a
/// domain does not carry yet (credit-note reason, requester and refund, the replacement link, the PDF records) are shadow
/// properties for E6.4b/E6.5. The buyer BIN is encrypted inside its snapshot, as in <c>companies</c> and quote snapshots.
/// </summary>
internal sealed class InvoiceConfiguration : IEntityTypeConfiguration<Invoice>
{
    private const string Specimen = "series IN ('SpecimenTaxInvoice', 'SpecimenCreditNote')";

    private sealed record BuyerJson(string Name, string? Bin, string? Address);

    public void Configure(EntityTypeBuilder<Invoice> b)
    {
        b.ToTable("invoices", t =>
        {
            t.HasCheckConstraint("ck_invoices_kind_series",
                "(kind = 'TaxInvoice' AND series IN ('TaxInvoice', 'SpecimenTaxInvoice') OR kind = 'CreditNote' AND series IN ('CreditNote', 'SpecimenCreditNote')) " +
                $"AND mode IN ('Live', 'Specimen') AND (mode = 'Specimen') = ({Specimen})");
            // The number is exactly what InvoiceNumber.Format prints for series, fiscal year and serial (2-6 capitals, SPEC only for specimens).
            t.HasCheckConstraint("ck_invoices_number",
                $"fiscal_year BETWEEN {FiscalYear.Min} AND {FiscalYear.Max} AND serial >= 1 AND split_part(number, '-', 1) ~ '^[A-Z]{{2,6}}$' " +
                $"AND (split_part(number, '-', 1) = '{InvoiceNumber.SpecimenPrefix}') = ({Specimen}) " +
                "AND number = split_part(number, '-', 1) || CASE WHEN kind = 'TaxInvoice' THEN '-INV-' ELSE '-CN-' END || fiscal_year::text || '-' " +
                "|| lpad(((fiscal_year + 1) % 100)::text, 2, '0') || '-' || CASE WHEN serial < 1000000 THEN lpad(serial::text, 6, '0') ELSE serial::text END");
            // §16 kind shape: a tax invoice is of a shipment; a credit note of an original, with a reason, requested by someone else.
            t.HasCheckConstraint("ck_invoices_kind_shape",
                "(kind = 'TaxInvoice' AND shipment_id IS NOT NULL AND original_invoice_id IS NULL AND reason IS NULL AND reason_text IS NULL " +
                "AND requested_by IS NULL AND refund_due_minor IS NULL) " +
                "OR (kind = 'CreditNote' AND shipment_id IS NULL AND original_invoice_id IS NOT NULL AND reason ~ '^[A-Za-z]{2,40}$' " +
                "AND requested_by IS NOT NULL AND requested_by <> issued_by AND refund_due_minor IS NOT NULL AND replacement_no = 0)");
            t.HasCheckConstraint("ck_invoices_links",
                "replacement_no >= 0 AND (replacement_no = 0) = (replaces_invoice_id IS NULL) " +
                "AND replaces_invoice_id IS DISTINCT FROM id AND original_invoice_id IS DISTINCT FROM id");
            t.HasCheckConstraint("ck_invoices_money",
                "net_minor >= 0 AND sd_minor >= 0 AND vat_minor >= 0 AND total_minor = net_minor + sd_minor + vat_minor " +
                "AND (refund_due_minor IS NULL OR refund_due_minor BETWEEN 0 AND total_minor)");
            t.HasCheckConstraint("ck_invoices_texts",
                "currency ~ '^[A-Z]{3}$' AND char_length(order_number) BETWEEN 1 AND 16 AND (reason_text IS NULL OR char_length(reason_text) <= 500) " +
                "AND jsonb_typeof(seller_snapshot) = 'object' AND jsonb_typeof(buyer_snapshot) = 'object' AND jsonb_typeof(delivery_snapshot) = 'object'");
            t.HasCheckConstraint("ck_invoices_hash", "content_hash ~ '^[0-9a-f]{64}$'");
            t.HasCheckConstraint("ck_invoices_pdf",
                "(pdf_en_key IS NULL) = (pdf_en_sha256 IS NULL) AND (pdf_bn_key IS NULL) = (pdf_bn_sha256 IS NULL) " +
                "AND (pdf_en_sha256 IS NULL OR pdf_en_sha256 ~ '^[0-9a-f]{64}$') AND (pdf_bn_sha256 IS NULL OR pdf_bn_sha256 ~ '^[0-9a-f]{64}$')");
        });
        b.MapAudit(updated: false);
        b.Xmin();
        b.Property(i => i.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.Mode).HasColumnName("mode").HasConversion<string>().HasMaxLength(20);
        b.Property(i => i.Number).HasColumnName("number").HasMaxLength(40).IsRequired();
        b.Property(i => i.SellerId).HasColumnName("seller_id");
        b.Property(i => i.Series).HasColumnName("series").HasConversion<string>().HasMaxLength(24);
        b.Property(i => i.FiscalYear).HasColumnName("fiscal_year");
        b.Property(i => i.Serial).HasColumnName("serial");
        b.Property(i => i.IssuedAtUtc).HasColumnName("issued_at");
        b.Property(i => i.OrderId).HasColumnName("order_id");
        b.Property(i => i.SubOrderId).HasColumnName("sub_order_id");
        b.Property(i => i.ShipmentId).HasColumnName("shipment_id");
        b.Property(i => i.ReplacementNo).HasColumnName("replacement_no");
        b.Property<Guid?>("ReplacesInvoiceId").HasColumnName("replaces_invoice_id");
        b.Property(i => i.OriginalInvoiceId).HasColumnName("original_invoice_id");
        b.Property<string?>("Reason").HasColumnName("reason").HasMaxLength(40);
        b.Property<string?>("ReasonText").HasColumnName("reason_text").HasMaxLength(500);
        b.Property(i => i.CompanyId).HasColumnName("company_id");
        b.Property(i => i.Seller).Jsonb("seller_snapshot", v => QuoteColumns.Json(v), s => QuoteColumns.FromJson<InvoiceSellerSnapshot>(s), (x, y) => Equals(x, y));
        b.Property(i => i.Buyer).Jsonb("buyer_snapshot",
            v => QuoteColumns.Json(new BuyerJson(v.Name, v.Bin is null ? null : FieldEncryption.Current.Protect(v.Bin), v.Address)),
            s => ReadBuyer(QuoteColumns.FromJson<BuyerJson>(s)), (x, y) => Equals(x, y));
        b.Property(i => i.Delivery).Jsonb("delivery_snapshot", v => QuoteColumns.Json(v), s => QuoteColumns.FromJson<InvoiceDeliverySnapshot>(s), (x, y) => Equals(x, y));
        b.Property(i => i.PurchaseOrderNumber).HasColumnName("purchase_order_number").HasMaxLength(Order.PurchaseOrderNumberMaxLength);
        b.Property(i => i.QuoteNumber).HasColumnName("quote_number").HasMaxLength(40);
        b.Property(i => i.OrderNumber).HasColumnName("order_number").HasMaxLength(16).IsRequired();
        b.Property(i => i.Currency).HasColumnName("currency").HasMaxLength(3).IsFixedLength();
        b.Property(i => i.NetMinor).HasColumnName("net_minor");
        b.Property(i => i.SdMinor).HasColumnName("sd_minor");
        b.Property(i => i.VatMinor).HasColumnName("vat_minor");
        b.Property(i => i.TotalMinor).HasColumnName("total_minor");
        b.Property<long?>("RefundDueMinor").HasColumnName("refund_due_minor");
        b.Property(i => i.ConsultantChecked).HasColumnName("consultant_checked");
        b.Property(i => i.ContentHash).HasColumnName("content_hash").HasMaxLength(64).IsFixedLength();
        b.Property(i => i.IssuedBy).HasColumnName("issued_by");
        b.Property<Guid?>("RequestedBy").HasColumnName("requested_by");
        // §12: written null → value once by the PDF store (E6.4b); the guard refuses every other UPDATE.
        foreach (var (name, locale) in new[] { ("En", "en"), ("Bn", "bn") })
        {
            b.Property<string?>($"Pdf{name}Key").HasColumnName($"pdf_{locale}_key").HasMaxLength(200);
            b.Property<string?>($"Pdf{name}Sha256").HasColumnName($"pdf_{locale}_sha256").HasMaxLength(64).IsFixedLength();
        }

        b.HasOne<Seller>().WithMany().HasForeignKey(i => i.SellerId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Order>().WithMany().HasForeignKey(i => i.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<SubOrder>().WithMany().HasForeignKey(i => i.SubOrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Shipment>().WithMany().HasForeignKey(i => i.ShipmentId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Company>().WithMany().HasForeignKey(i => i.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey(i => i.OriginalInvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Invoice>().WithMany().HasForeignKey("ReplacesInvoiceId").OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(i => i.IssuedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey("RequestedBy").OnDelete(DeleteBehavior.Restrict);
        b.HasMany(i => i.Lines).WithOne().HasForeignKey(l => l.InvoiceId).OnDelete(DeleteBehavior.Restrict);
        b.Navigation(i => i.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(i => i.Number).IsUnique().HasDatabaseName("ux_invoices_number");
        b.HasIndex(i => new { i.SellerId, i.Series, i.FiscalYear, i.Serial }).IsUnique().HasDatabaseName("ux_invoices_serial");
        // IT9 backstop (§15): one tax invoice per shipment and seller; a replacement (§9.4) takes the next replacement_no.
        b.HasIndex(i => new { i.ShipmentId, i.SellerId, i.ReplacementNo }).IsUnique().HasFilter("kind = 'TaxInvoice'")
            .HasDatabaseName("ux_invoices_one_per_shipment");
        b.HasIndex(i => i.OrderId).HasDatabaseName("ix_invoices_order");
        b.HasIndex(i => new { i.CompanyId, i.IssuedAtUtc }).HasFilter("company_id IS NOT NULL").HasDatabaseName("ix_invoices_company_issued");
        b.HasIndex(i => i.OriginalInvoiceId).HasFilter("original_invoice_id IS NOT NULL").HasDatabaseName("ix_invoices_original");
    }

    private static InvoiceBuyerSnapshot ReadBuyer(BuyerJson j) => new(j.Name, j.Bin is null ? null : FieldEncryption.Current.Unprotect(j.Bin), j.Address);
}

/// <summary>E6.3b invoice lines: insert-only (<see cref="InvoiceSql"/>), money in bigint poisha, the order line and the credited line by RESTRICT FK.</summary>
internal sealed class InvoiceLineConfiguration : IEntityTypeConfiguration<InvoiceLine>
{
    public void Configure(EntityTypeBuilder<InvoiceLine> b)
    {
        b.ToTable("invoice_lines", t =>
        {
            t.HasCheckConstraint("ck_invoice_lines_shape",
                "line_no >= 1 AND quantity >= 1 AND (kind = 'Goods' AND sku IS NOT NULL OR kind = 'Delivery' AND order_item_id IS NULL AND quantity = 1) " +
                "AND char_length(btrim(description_en)) >= 1 AND char_length(btrim(description_bn)) >= 1 AND char_length(btrim(unit)) >= 1");
            // Exactly one printed unit price: exact (net basis, delivery) or the display-only rounded one of a personal order (§7).
            t.HasCheckConstraint("ck_invoice_lines_unit_price",
                "(unit_price_minor IS NULL) <> (display_unit_price_minor IS NULL) AND COALESCE(unit_price_minor, display_unit_price_minor) >= 0");
            t.HasCheckConstraint("ck_invoice_lines_money",
                "gross_minor >= 0 AND discount_minor >= 0 AND net_minor >= 0 AND sd_minor >= 0 AND vat_minor >= 0 " +
                "AND total_minor = net_minor + sd_minor + vat_minor AND sd_rate BETWEEN 0 AND 1 AND vat_rate BETWEEN 0 AND 1");
        });
        b.MapAudit(updated: false);
        b.Property(l => l.InvoiceId).HasColumnName("invoice_id");
        b.Property(l => l.LineNo).HasColumnName("line_no");
        b.Property(l => l.Kind).HasColumnName("kind").HasConversion<string>().HasMaxLength(20);
        b.Property(l => l.OrderItemId).HasColumnName("order_item_id");
        b.Property(l => l.OriginalLineId).HasColumnName("original_line_id");
        b.Property(l => l.Sku).HasColumnName("sku").HasMaxLength(64);
        b.Property(l => l.PartNumber).HasColumnName("part_number").HasMaxLength(100);
        b.Property(l => l.DescriptionEn).HasColumnName("description_en").HasMaxLength(255).IsRequired();
        b.Property(l => l.DescriptionBn).HasColumnName("description_bn").HasMaxLength(255).IsRequired();
        b.Property(l => l.Unit).HasColumnName("unit").HasMaxLength(20).IsRequired();
        b.Property(l => l.Quantity).HasColumnName("quantity");
        b.Property(l => l.UnitPriceMinor).HasColumnName("unit_price_minor");
        b.Property(l => l.DisplayUnitPriceMinor).HasColumnName("display_unit_price_minor");
        b.Property(l => l.GrossMinor).HasColumnName("gross_minor");
        b.Property(l => l.DiscountMinor).HasColumnName("discount_minor");
        b.Property(l => l.NetMinor).HasColumnName("net_minor");
        b.Property(l => l.SdRate).HasColumnName("sd_rate").HasPrecision(5, 4);
        b.Property(l => l.SdMinor).HasColumnName("sd_minor");
        b.Property(l => l.VatRate).HasColumnName("vat_rate").HasPrecision(5, 4);
        b.Property(l => l.VatMinor).HasColumnName("vat_minor");
        b.Property(l => l.TotalMinor).HasColumnName("total_minor");
        b.HasOne<OrderItem>().WithMany().HasForeignKey(l => l.OrderItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<InvoiceLine>().WithMany().HasForeignKey(l => l.OriginalLineId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(l => new { l.InvoiceId, l.LineNo }).IsUnique().HasDatabaseName("ux_invoice_lines_line_no");
        b.HasIndex(l => l.OrderItemId).HasFilter("order_item_id IS NOT NULL").HasDatabaseName("ix_invoice_lines_order_item");
        b.HasIndex(l => l.OriginalLineId).HasFilter("original_line_id IS NOT NULL").HasDatabaseName("ix_invoice_lines_original");
    }
}
