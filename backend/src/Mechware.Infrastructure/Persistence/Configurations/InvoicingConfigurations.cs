using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Domain.Invoicing;
using Mechware.Domain.Sellers;

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
