using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Domain.Catalog;
using Mechware.Domain.Common;
using Mechware.Domain.Companies;
using Mechware.Domain.Pricing.PriceLists;

namespace Mechware.Infrastructure.Persistence.Configurations;

/// <summary>
/// Price lists (ADR-0008 §1, §3). The CHECKs below are the database backstop of the aggregate's rules; the triggers that
/// make approved and withdrawn versions immutable and enforce "no contributor approves" live in <see cref="PriceListSql"/>.
/// </summary>
internal sealed class PriceListConfiguration : IEntityTypeConfiguration<PriceList>
{
    private const string Open = "'Draft', 'PendingApproval'";

    public void Configure(EntityTypeBuilder<PriceList> b)
    {
        b.ToTable("price_lists", t =>
        {
            t.HasCheckConstraint("ck_price_lists_version", "version >= 1");
            t.HasCheckConstraint("ck_price_lists_scope", "scope IN ('General', 'Group', 'Company')");
            t.HasCheckConstraint("ck_price_lists_status", $"status IN ({Open}, 'Approved', 'Withdrawn')");
            t.HasCheckConstraint("ck_price_lists_currency", "currency = 'BDT'");
            t.HasCheckConstraint("ck_price_lists_company_shape",
                "(scope = 'Company' AND company_id IS NOT NULL AND contract_ref IS NOT NULL) " +
                "OR (scope <> 'Company' AND company_id IS NULL AND contract_ref IS NULL)");
            t.HasCheckConstraint("ck_price_lists_window",
                $"valid_to > valid_from AND valid_to - valid_from <= interval '{PriceListLimits.MaxValidityDays} days'");
            t.HasCheckConstraint("ck_price_lists_content_hash", "content_hash IS NULL OR content_hash ~ '^[0-9a-f]{64}$'");
            // Four eyes (PL1): never the creator, never the submitter. Contributors are checked by a trigger.
            t.HasCheckConstraint("ck_price_lists_four_eyes",
                "approved_by IS NULL OR (submitted_by IS NOT NULL AND approved_by <> created_by AND approved_by <> submitted_by)");
            // Which columns each stored status carries.
            t.HasCheckConstraint("ck_price_lists_status_shape",
                "(status = 'Draft' AND submitted_by IS NULL AND submitted_at IS NULL AND content_hash IS NULL) " +
                "OR (status <> 'Draft' AND submitted_by IS NOT NULL AND submitted_at IS NOT NULL AND content_hash IS NOT NULL)");
            t.HasCheckConstraint("ck_price_lists_approval_shape",
                "(status IN ('Approved', 'Withdrawn')) = (approved_by IS NOT NULL) " +
                "AND (approved_by IS NULL) = (approved_at IS NULL) AND (approved_by IS NULL) = (effective_from_utc IS NULL)");
            t.HasCheckConstraint("ck_price_lists_withdrawal_shape",
                "(status = 'Withdrawn') = (withdrawn_by IS NOT NULL) " +
                "AND (withdrawn_by IS NULL) = (withdrawn_at IS NULL) AND (withdrawn_by IS NULL) = (withdraw_reason IS NULL)");
            t.HasCheckConstraint("ck_price_lists_rejection_shape",
                "(rejected_by IS NULL) = (rejected_at IS NULL) AND (rejected_by IS NULL) = (rejection_reason IS NULL)");
            // §10: a list starts no earlier than its window and the activation delay after approval (no backdating).
            t.HasCheckConstraint("ck_price_lists_effective_from",
                "effective_from_utc IS NULL OR (effective_from_utc >= valid_from " +
                $"AND effective_from_utc >= approved_at + interval '{(int)PriceListLimits.ActivationDelay.TotalSeconds} seconds' " +
                "AND effective_from_utc < valid_to)");
        });
        b.MapAudit();
        b.IgnoreEvents();
        // Optimistic concurrency on drafts (§10); approval and withdrawal also lock the row FOR UPDATE (D2.8).
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Property(p => p.SeriesId).HasColumnName("series_id");
        b.Property(p => p.Version).HasColumnName("version");
        b.Property(p => p.Name).HasColumnName("name").HasMaxLength(PriceList.MaxNameLength).IsRequired();
        b.Property(p => p.Scope).HasColumnName("scope").HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.CompanyId).HasColumnName("company_id");
        b.Property(p => p.ContractRef).HasColumnName("contract_ref").HasMaxLength(PriceList.MaxContractRefLength);
        b.Property(p => p.Currency).HasColumnName("currency").HasConversion<string>().HasMaxLength(3);
        b.Property(p => p.ValidFromUtc).HasColumnName("valid_from");
        b.Property(p => p.ValidToUtc).HasColumnName("valid_to");
        b.Property(p => p.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20);
        b.Property(p => p.CreatedBy).HasColumnName("created_by");
        b.Property(p => p.SubmittedBy).HasColumnName("submitted_by");
        b.Property(p => p.SubmittedAtUtc).HasColumnName("submitted_at");
        b.Property(p => p.ContentHash).HasColumnName("content_hash").HasMaxLength(64).IsFixedLength();
        b.Property(p => p.ApprovedBy).HasColumnName("approved_by");
        b.Property(p => p.ApprovedAtUtc).HasColumnName("approved_at");
        // Stored as the aggregate computed it (PriceResolver.EffectiveFromUtc); never recomputed in SQL.
        b.Property(p => p.EffectiveFromUtc).HasColumnName("effective_from_utc");
        b.Property(p => p.RejectedBy).HasColumnName("rejected_by");
        b.Property(p => p.RejectedAtUtc).HasColumnName("rejected_at");
        b.Property(p => p.RejectionReason).HasColumnName("rejection_reason").HasMaxLength(PriceList.MaxReasonLength);
        b.Property(p => p.WithdrawnBy).HasColumnName("withdrawn_by");
        b.Property(p => p.WithdrawnAtUtc).HasColumnName("withdrawn_at");
        b.Property(p => p.WithdrawReason).HasColumnName("withdraw_reason").HasMaxLength(PriceList.MaxReasonLength);

        b.HasOne<Company>().WithMany().HasForeignKey(p => p.CompanyId).OnDelete(DeleteBehavior.Restrict);
        b.HasMany(p => p.Entries).WithOne().HasForeignKey(e => e.PriceListId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(p => p.Contributors).WithOne().HasForeignKey(c => c.PriceListId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(p => p.Entries).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(p => p.Contributors).UsePropertyAccessMode(PropertyAccessMode.Field);

        b.HasIndex(p => new { p.SeriesId, p.Version }).IsUnique().HasDatabaseName("ux_price_lists_series_version");
        // One open (Draft or PendingApproval) version per series (§3).
        b.HasIndex(p => p.SeriesId).IsUnique().HasFilter($"status IN ({Open})").HasDatabaseName("ux_price_lists_one_open_version");
        b.HasIndex(p => new { p.CompanyId, p.Status }).HasDatabaseName("ix_price_lists_company_status");
        b.HasIndex(p => new { p.Scope, p.Status }).HasDatabaseName("ix_price_lists_scope_status");
    }
}

internal sealed class PriceListEntryConfiguration : IEntityTypeConfiguration<PriceListEntry>
{
    public void Configure(EntityTypeBuilder<PriceListEntry> b)
    {
        b.ToTable("price_list_entries", t =>
        {
            t.HasCheckConstraint("ck_price_list_entries_price", $"unit_price_minor > 0 AND unit_price_minor <= {PriceListLimits.MaxUnitPriceMinor}");
            t.HasCheckConstraint("ck_price_list_entries_min_qty", $"min_qty >= 1 AND min_qty <= {OrderLimits.MaxQuantityPerLine}");
            t.HasCheckConstraint("ck_price_list_entries_reason",
                $"below_floor_reason IS NULL OR char_length(below_floor_reason) BETWEEN {PriceList.MinReasonLength} AND {PriceList.MaxReasonLength}");
            t.HasCheckConstraint("ck_price_list_entries_approval_values",
                "(cost_minor_at_approval IS NULL OR cost_minor_at_approval > 0) AND (floor_minor_at_approval IS NULL OR floor_minor_at_approval > 0)");
        });
        b.MapAudit(updated: false);
        b.Property(e => e.PriceListId).HasColumnName("price_list_id");
        b.Property(e => e.VariantId).HasColumnName("variant_id");
        b.Property(e => e.MinQty).HasColumnName("min_qty");
        b.Property(e => e.UnitPriceMinor).HasColumnName("unit_price_minor");
        b.Property(e => e.BelowFloorReason).HasColumnName("below_floor_reason").HasMaxLength(PriceList.MaxReasonLength);
        b.Property(e => e.CostMinorAtApproval).HasColumnName("cost_minor_at_approval");
        b.Property(e => e.FloorMinorAtApproval).HasColumnName("floor_minor_at_approval");
        b.Property(e => e.BelowFloorAtApproval).HasColumnName("below_floor_at_approval");

        b.HasOne<ProductVariant>().WithMany().HasForeignKey(e => e.VariantId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(e => new { e.PriceListId, e.VariantId, e.MinQty }).IsUnique().HasDatabaseName("ux_price_list_entries_variant_min_qty");
        b.HasIndex(e => e.VariantId).HasDatabaseName("ix_price_list_entries_variant");
    }
}

internal sealed class PriceListContributorConfiguration : IEntityTypeConfiguration<PriceListContributor>
{
    public void Configure(EntityTypeBuilder<PriceListContributor> b)
    {
        b.ToTable("price_list_contributors");
        b.HasKey(c => new { c.PriceListId, c.UserId });
        b.Property(c => c.PriceListId).HasColumnName("price_list_id");
        b.Property(c => c.UserId).HasColumnName("user_id");
        b.Property(c => c.FirstContributedAtUtc).HasColumnName("first_contributed_at");
    }
}

internal sealed class MarginFloorConfiguration : IEntityTypeConfiguration<MarginFloor>
{
    public void Configure(EntityTypeBuilder<MarginFloor> b)
    {
        b.ToTable("margin_floors", t =>
        {
            t.HasCheckConstraint("ck_margin_floors_shape",
                "(scope = 'Global' AND brand_id IS NULL AND category_id IS NULL) " +
                "OR (scope = 'Category' AND brand_id IS NULL AND category_id IS NOT NULL) " +
                "OR (scope = 'Brand' AND brand_id IS NOT NULL AND category_id IS NULL) " +
                "OR (scope = 'BrandAndCategory' AND brand_id IS NOT NULL AND category_id IS NOT NULL)");
            // 0 … 1,000 % with at most two decimals, refused rather than rounded.
            t.HasCheckConstraint("ck_margin_floors_markup",
                $"min_markup_percent >= 0 AND min_markup_percent <= {(int)MarginFloorPolicy.MaxMinMarkupPercent} " +
                "AND min_markup_percent = round(min_markup_percent, 2)");
        });
        b.MapAudit();
        b.Property(f => f.Scope).HasColumnName("scope").HasConversion<string>().HasMaxLength(20);
        b.Property(f => f.BrandId).HasColumnName("brand_id");
        b.Property(f => f.CategoryId).HasColumnName("category_id");
        b.Property(f => f.MinMarkupPercent).HasColumnName("min_markup_percent").HasColumnType("numeric");
        b.Property(f => f.UpdatedBy).HasColumnName("updated_by");

        b.HasOne<Brand>().WithMany().HasForeignKey(f => f.BrandId).OnDelete(DeleteBehavior.Restrict);
        b.HasOne<Category>().WithMany().HasForeignKey(f => f.CategoryId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(f => new { f.Scope, f.BrandId, f.CategoryId }).IsUnique().AreNullsDistinct(false)
            .HasDatabaseName("ux_margin_floors_scope_brand_category");
    }
}
