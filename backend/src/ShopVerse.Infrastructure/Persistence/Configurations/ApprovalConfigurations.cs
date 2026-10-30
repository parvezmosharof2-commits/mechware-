using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShopVerse.Domain.Approvals;
using ShopVerse.Domain.Companies;

namespace ShopVerse.Infrastructure.Persistence.Configurations;

internal sealed class ApprovalRuleConfiguration : IEntityTypeConfiguration<ApprovalRule>
{
    public void Configure(EntityTypeBuilder<ApprovalRule> b)
    {
        b.ToTable("approval_rules", t =>
        {
            t.HasCheckConstraint("ck_approval_rules_min_amount", "min_amount >= 0");
            t.HasCheckConstraint("ck_approval_rules_chain", "cardinality(approver_user_ids) BETWEEN 1 AND 5");
        });
        b.MapAudit();
        b.Property(r => r.CompanyId).HasColumnName("company_id");
        b.Property(r => r.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        b.Property(r => r.MinAmountMinor).HasColumnName("min_amount");
        b.Property(r => r.CostCentreId).HasColumnName("cost_centre_id");
        b.Property(r => r.CategoryIds).HasColumnName("category_ids").HasColumnType("uuid[]");
        b.Property(r => r.ApproverUserIds).HasColumnName("approver_user_ids").HasColumnType("uuid[]");
        b.Property(r => r.IsActive).HasColumnName("is_active");

        b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(r => r.CompanyId).HasDatabaseName("ix_approval_rules_company");
    }
}

internal sealed class ApprovalRequestConfiguration : IEntityTypeConfiguration<ApprovalRequest>
{
    public void Configure(EntityTypeBuilder<ApprovalRequest> b)
    {
        b.ToTable("approval_requests", t => t.HasCheckConstraint("ck_approval_requests_amount", "amount > 0"));
        b.MapAudit();
        b.IgnoreEvents();
        b.Ignore(r => r.CurrentStep);
        // Concurrency token: two approvers deciding at once cannot both win (Decide always touches updated_at).
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Property(r => r.CompanyId).HasColumnName("company_id");
        b.Property(r => r.RequesterUserId).HasColumnName("requester_user_id");
        b.Property(r => r.SubjectType).HasColumnName("subject_type").HasMaxLength(40).IsRequired();
        b.Property(r => r.SubjectId).HasColumnName("subject_id");
        b.Property(r => r.AmountMinor).HasColumnName("amount");
        b.Property(r => r.CostCentreId).HasColumnName("cost_centre_id");
        b.Property(r => r.CategoryIds).HasColumnName("category_ids").HasColumnType("uuid[]");
        b.Property(r => r.RuleId).HasColumnName("rule_id");
        b.Property(r => r.RuleName).HasColumnName("rule_name").HasMaxLength(100).IsRequired();
        b.Property(r => r.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();

        b.HasOne<Company>().WithMany().HasForeignKey(r => r.CompanyId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(r => r.Steps).WithOne().HasForeignKey(s => s.RequestId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(r => r.Steps).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(r => new { r.CompanyId, r.Status }).HasDatabaseName("ix_approval_requests_company_status");
        b.HasIndex(r => new { r.CompanyId, r.RequesterUserId }).HasDatabaseName("ix_approval_requests_requester");
        // One open request per subject: resubmitting the same order cannot start a second chain.
        b.HasIndex(r => new { r.CompanyId, r.SubjectType, r.SubjectId }).IsUnique()
            .HasFilter("status = 'Pending'").HasDatabaseName("ux_approval_requests_pending_subject");
    }
}

internal sealed class ApprovalStepConfiguration : IEntityTypeConfiguration<ApprovalStep>
{
    public void Configure(EntityTypeBuilder<ApprovalStep> b)
    {
        b.ToTable("approval_steps");
        b.MapAudit();
        b.Property(s => s.RequestId).HasColumnName("request_id");
        b.Property(s => s.Position).HasColumnName("position");
        b.Property(s => s.ApproverUserId).HasColumnName("approver_user_id");
        b.Property(s => s.Decision).HasColumnName("decision").HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(s => s.Comment).HasColumnName("comment").HasMaxLength(500);
        b.Property(s => s.DecidedAtUtc).HasColumnName("decided_at");

        b.HasIndex(s => new { s.RequestId, s.Position }).IsUnique().HasDatabaseName("ux_approval_steps_position");
        b.HasIndex(s => s.ApproverUserId).HasDatabaseName("ix_approval_steps_approver");
    }
}
