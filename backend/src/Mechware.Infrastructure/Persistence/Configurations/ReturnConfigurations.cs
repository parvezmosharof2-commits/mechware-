using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Domain.Companies;
using Mechware.Domain.Identity;
using Mechware.Domain.Orders;
using Mechware.Domain.Returns;

namespace Mechware.Infrastructure.Persistence.Configurations;

/// <summary>F2.1 return requests. The CHECKs repeat the domain's rules; <see cref="ReturnSql"/> adds the quantity guard.</summary>
internal sealed class ReturnRequestConfiguration : IEntityTypeConfiguration<ReturnRequest>
{
    public void Configure(EntityTypeBuilder<ReturnRequest> b)
    {
        b.ToTable("return_requests", t =>
        {
            t.HasCheckConstraint("ck_return_requests_status",
                "status IN ('Requested', 'Approved', 'Rejected', 'Received', 'Inspected', 'Refunded', 'CreditNoted', 'Withdrawn')");
            // F2.3a: who and when travel together, and exist exactly from the step that sets them on.
            t.HasCheckConstraint("ck_return_requests_decided",
                "(decided_by IS NULL) = (decided_at IS NULL) AND (decided_at IS NULL) = (status IN ('Requested', 'Withdrawn'))");
            t.HasCheckConstraint("ck_return_requests_rejection",
                "(status = 'Rejected') = (rejection_reason IS NOT NULL) AND (rejection_reason IS NULL OR "
                + "(char_length(rejection_reason) BETWEEN 5 AND 500 AND rejection_reason = btrim(rejection_reason)))");
            t.HasCheckConstraint("ck_return_requests_received",
                "(received_by IS NULL) = (received_at IS NULL) AND (received_at IS NULL) = (status IN ('Requested', 'Withdrawn', 'Approved', 'Rejected'))");
            t.HasCheckConstraint("ck_return_requests_inspected",
                "(inspected_by IS NULL) = (inspected_at IS NULL) AND (inspected_at IS NULL) = (status NOT IN ('Inspected', 'Refunded', 'CreditNoted'))");
            t.HasCheckConstraint("ck_return_requests_reason",
                "reason IN ('Damaged', 'WrongItem', 'NotAsDescribed', 'Other') AND (reason <> 'Other' OR note IS NOT NULL)");
            t.HasCheckConstraint("ck_return_requests_note", "note IS NULL OR (char_length(note) BETWEEN 1 AND 1000 AND note = btrim(note))");
            t.HasCheckConstraint("ck_return_requests_key", "idempotency_key ~ '^[A-Za-z0-9_-]{8,100}$' AND char_length(request_hash) = 64");
            t.HasCheckConstraint("ck_return_requests_withdrawn", "(status = 'Withdrawn') = (withdrawn_at IS NOT NULL)");
        });
        b.MapAudit();
        b.IgnoreEvents();
        b.Property(r => r.OrderId).HasColumnName("order_id");
        b.Property(r => r.CompanyId).HasColumnName("company_id");
        b.Property(r => r.RequestedBy).HasColumnName("requested_by");
        b.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.Reason).HasColumnName("reason").HasConversion<string>().HasMaxLength(20);
        b.Property(r => r.Note).HasColumnName("note").HasMaxLength(ReturnRequest.NoteMaxLength);
        b.Property(r => r.IdempotencyKey).HasColumnName("idempotency_key").HasMaxLength(100).IsRequired();
        b.Property(r => r.RequestHash).HasColumnName("request_hash").HasMaxLength(64).IsRequired();
        b.Property(r => r.RequestedAtUtc).HasColumnName("requested_at");
        b.Property(r => r.WithdrawnAtUtc).HasColumnName("withdrawn_at");
        b.Property(r => r.DecidedBy).HasColumnName("decided_by");
        b.Property(r => r.DecidedAtUtc).HasColumnName("decided_at");
        b.Property(r => r.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(ReturnRequest.ReasonMaxLength);
        b.Property(r => r.ReceivedBy).HasColumnName("received_by");
        b.Property(r => r.ReceivedAtUtc).HasColumnName("received_at");
        b.Property(r => r.InspectedBy).HasColumnName("inspected_by");
        b.Property(r => r.InspectedAtUtc).HasColumnName("inspected_at");
        // The staff decisions' rowVersion (F2.3a): Postgres' row version, never stored by us.
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.HasOne<User>().WithMany().HasForeignKey(r => r.DecidedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(r => r.ReceivedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(r => r.InspectedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Order>().WithMany().HasForeignKey(r => r.OrderId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<User>().WithMany().HasForeignKey(r => r.RequestedBy).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(r => r.Lines).WithOne().HasForeignKey(l => l.ReturnRequestId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(r => r.Lines).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.HasIndex(r => new { r.RequestedBy, r.IdempotencyKey }).IsUnique().HasDatabaseName("ux_return_requests_idempotency");
        b.HasIndex(r => r.OrderId).HasDatabaseName("ix_return_requests_order");
        b.HasIndex(r => new { r.RequestedBy, r.RequestedAtUtc }).HasDatabaseName("ix_return_requests_requester");
        b.HasIndex(r => new { r.Status, r.RequestedAtUtc }).HasDatabaseName("ix_return_requests_status");
    }
}

internal sealed class ReturnRequestLineConfiguration : IEntityTypeConfiguration<ReturnRequestLine>
{
    public void Configure(EntityTypeBuilder<ReturnRequestLine> b)
    {
        b.ToTable("return_request_lines", t =>
        {
            t.HasCheckConstraint("ck_return_request_lines_qty", "quantity BETWEEN 1 AND 10000");
            // F2.3a inspection: all three empty, or accepted 0..asked, restocked 0..accepted, note trimmed 1-500.
            t.HasCheckConstraint("ck_return_request_lines_inspection",
                "(accepted_quantity IS NULL AND restock_quantity IS NULL AND inspection_note IS NULL) OR "
                + "(accepted_quantity BETWEEN 0 AND quantity AND restock_quantity BETWEEN 0 AND accepted_quantity AND (inspection_note IS NULL OR "
                + "(char_length(inspection_note) BETWEEN 1 AND 500 AND inspection_note = btrim(inspection_note))))");
        });
        b.MapAudit(updated: false);
        b.Property(l => l.ReturnRequestId).HasColumnName("return_request_id");
        b.Property(l => l.OrderItemId).HasColumnName("order_item_id");
        b.Property(l => l.Quantity).HasColumnName("quantity");
        b.Property(l => l.AcceptedQuantity).HasColumnName("accepted_quantity");
        b.Property(l => l.RestockQuantity).HasColumnName("restock_quantity");
        b.Property(l => l.InspectionNote).HasColumnName("inspection_note").HasMaxLength(ReturnRequest.InspectionNoteMaxLength);
        b.HasOne<OrderItem>().WithMany().HasForeignKey(l => l.OrderItemId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(l => new { l.ReturnRequestId, l.OrderItemId }).IsUnique().HasDatabaseName("ux_return_request_lines_item");
        b.HasIndex(l => l.OrderItemId).HasDatabaseName("ix_return_request_lines_order_item");
    }
}
