using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShopVerse.Domain.Common.Enums;
using ShopVerse.Domain.Companies;

namespace ShopVerse.Infrastructure.Persistence.Configurations;

internal sealed class CompanyConfiguration : IEntityTypeConfiguration<Company>
{
    public void Configure(EntityTypeBuilder<Company> b)
    {
        b.ToTable("companies", t =>
        {
            t.HasCheckConstraint("ck_companies_credit_non_negative", "credit_limit >= 0 AND credit_used >= 0");
            t.HasCheckConstraint("ck_companies_credit_within_limit", "credit_used <= credit_limit");
            t.HasCheckConstraint("ck_companies_terms_limit", "credit_terms <> 0 OR credit_limit = 0");
        });
        b.MapAudit();
        b.IgnoreEvents();
        b.Ignore(c => c.CreditLimit);
        b.Ignore(c => c.CreditUsed);
        b.Ignore(c => c.CreditAvailable);
        b.Ignore(c => c.IsActive);
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Property(c => c.LegalName).HasColumnName("legal_name").HasMaxLength(200).IsRequired();
        b.Property(c => c.TradeLicenceNo).HasColumnName("trade_licence_no").HasMaxLength(50).IsRequired();
        b.Property(c => c.Bin).HasColumnName("bin").HasMaxLength(13).IsRequired();
        b.Property(c => c.Tin).HasColumnName("tin").HasMaxLength(12);
        b.Property(c => c.Industry).HasColumnName("industry").HasMaxLength(100);
        b.Property(c => c.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(20).IsRequired();
        b.Property(c => c.CreditTerms).HasColumnName("credit_terms").HasConversion<int>();
        b.Property(c => c.Currency).HasColumnName("currency").HasConversion<string>().HasMaxLength(3).IsRequired();
        b.Property(c => c.CreditLimitMinor).HasColumnName("credit_limit");
        b.Property(c => c.CreditUsedMinor).HasColumnName("credit_used");
        b.Property(c => c.DefaultPriceListId).HasColumnName("default_price_list_id");
        b.Property(c => c.AccountManagerId).HasColumnName("account_manager_id");

        b.HasIndex(c => c.Bin).IsUnique().HasDatabaseName("ux_companies_bin");
        b.HasIndex(c => c.Status).HasDatabaseName("ix_companies_status");
    }
}

internal sealed class CompanySiteConfiguration : IEntityTypeConfiguration<CompanySite>
{
    public void Configure(EntityTypeBuilder<CompanySite> b)
    {
        b.ToTable("company_sites");
        b.MapAudit();
        b.Property(s => s.CompanyId).HasColumnName("company_id");
        b.Property(s => s.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        b.Property(s => s.ContactName).HasColumnName("contact_name").HasMaxLength(150).IsRequired();
        b.Property(s => s.ContactPhone).HasColumnName("contact_phone").HasMaxLength(20).IsRequired();
        b.Property(s => s.Line1).HasColumnName("line1").HasMaxLength(255).IsRequired();
        b.Property(s => s.Line2).HasColumnName("line2").HasMaxLength(255);
        b.Property(s => s.Upazila).HasColumnName("upazila").HasMaxLength(100).IsRequired();
        b.Property(s => s.District).HasColumnName("district").HasMaxLength(100).IsRequired();
        b.Property(s => s.Division).HasColumnName("division").HasMaxLength(100).IsRequired();
        b.Property(s => s.PostalCode).HasColumnName("postal_code").HasMaxLength(20);
        b.Property(s => s.IsDefaultDelivery).HasColumnName("is_default_delivery");

        b.HasOne<Company>().WithMany().HasForeignKey(s => s.CompanyId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(s => s.CompanyId).HasDatabaseName("ix_company_sites_company");
        b.HasIndex(s => s.CompanyId).IsUnique().HasFilter("is_default_delivery").HasDatabaseName("ux_company_sites_one_default");
    }
}

internal sealed class CostCentreConfiguration : IEntityTypeConfiguration<CostCentre>
{
    public void Configure(EntityTypeBuilder<CostCentre> b)
    {
        b.ToTable("cost_centres", t => t.HasCheckConstraint("ck_cost_centres_budget", "monthly_budget IS NULL OR monthly_budget >= 0"));
        b.MapAudit();
        b.Property(c => c.CompanyId).HasColumnName("company_id");
        b.Property(c => c.Code).HasColumnName("code").HasMaxLength(30).IsRequired();
        b.Property(c => c.Name).HasColumnName("name").HasMaxLength(100).IsRequired();
        b.Property(c => c.MonthlyBudgetMinor).HasColumnName("monthly_budget");

        b.HasOne<Company>().WithMany().HasForeignKey(c => c.CompanyId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(c => new { c.CompanyId, c.Code }).IsUnique().HasDatabaseName("ux_cost_centres_company_code");
    }
}

internal sealed class CompanyMemberConfiguration : IEntityTypeConfiguration<CompanyMember>
{
    public void Configure(EntityTypeBuilder<CompanyMember> b)
    {
        b.ToTable("company_members", t =>
        {
            t.HasCheckConstraint("ck_company_members_roles", "roles BETWEEN 1 AND 15");
            t.HasCheckConstraint("ck_company_members_spend", "spend_limit IS NULL OR spend_limit >= 0");
        });
        b.MapAudit();
        b.Property<uint>("xmin").HasColumnName("xmin").HasColumnType("xid").ValueGeneratedOnAddOrUpdate().IsConcurrencyToken();
        b.Property(m => m.CompanyId).HasColumnName("company_id");
        b.Property(m => m.UserId).HasColumnName("user_id");
        b.Property(m => m.Roles).HasColumnName("roles").HasConversion<int>();
        b.Property(m => m.SpendLimitMinor).HasColumnName("spend_limit");
        b.Property(m => m.IsActive).HasColumnName("is_active");
        b.Property(m => m.CostCentreIds).HasColumnName("cost_centre_ids").HasColumnType("uuid[]");

        b.HasOne<Company>().WithMany().HasForeignKey(m => m.CompanyId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(m => new { m.CompanyId, m.UserId }).IsUnique().HasDatabaseName("ux_company_members_company_user");
        b.HasIndex(m => m.UserId).HasDatabaseName("ix_company_members_user");
    }
}
