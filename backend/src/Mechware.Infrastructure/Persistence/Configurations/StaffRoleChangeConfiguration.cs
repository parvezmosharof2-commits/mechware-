using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Domain.Identity;

namespace Mechware.Infrastructure.Persistence.Configurations;

/// <summary>G5.7 role-change requests. Four eyes is also a CHECK: nobody asks for or decides their own change, nobody approves their own request.</summary>
internal sealed class StaffRoleChangeConfiguration : IEntityTypeConfiguration<StaffRoleChange>
{
    public void Configure(EntityTypeBuilder<StaffRoleChange> b)
    {
        b.ToTable("staff_role_changes", t =>
        {
            t.HasCheckConstraint("ck_staff_role_changes_not_self", "requested_by <> user_id AND (decided_by IS NULL OR decided_by <> user_id)");
            t.HasCheckConstraint("ck_staff_role_changes_four_eyes", "status <> 'approved' OR (decided_by IS NOT NULL AND decided_by <> requested_by)");
        });
        b.MapAudit();
        b.Property(c => c.UserId).HasColumnName("user_id");
        b.Property(c => c.RolesBefore).HasColumnName("roles_before").HasColumnType("text[]");
        b.Property(c => c.RolesAfter).HasColumnName("roles_after").HasColumnType("text[]");
        b.Property(c => c.Reason).HasColumnName("reason").HasMaxLength(StaffRoleChange.MaxReasonLength).IsRequired();
        b.Property(c => c.Status).HasColumnName("status").HasConversion(new SnakeCaseEnumConverter<StaffRoleChangeStatus>()).HasMaxLength(20);
        b.Property(c => c.RequestedBy).HasColumnName("requested_by");
        b.Property(c => c.DecidedBy).HasColumnName("decided_by");
        b.Property(c => c.DecidedAtUtc).HasColumnName("decided_at");
        b.Property(c => c.DecisionReason).HasColumnName("decision_reason").HasMaxLength(StaffRoleChange.MaxReasonLength);

        b.HasOne<User>().WithMany().HasForeignKey(c => c.UserId).OnDelete(DeleteBehavior.Restrict);
        // One open request per person: a second one would be decided against roles the first may already have changed.
        b.HasIndex(c => c.UserId).IsUnique().HasFilter("status = 'requested'").HasDatabaseName("ux_staff_role_changes_open_user");
        b.HasIndex(c => new { c.Status, c.CreatedAtUtc }).HasDatabaseName("ix_staff_role_changes_status_created");
    }
}
