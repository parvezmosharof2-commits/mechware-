using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using ShopVerse.Domain.Identity;

namespace ShopVerse.Infrastructure.Persistence;

/// <summary>
/// EF Core DbContext for MECHWARE platform with Fluent API configurations for Phase 3 Identity & Access.
/// </summary>
public sealed class ShopVerseDbContext : DbContext
{
    public ShopVerseDbContext(DbContextOptions<ShopVerseDbContext> options) : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<Address> Addresses => Set<Address>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<Permission> Permissions => Set<Permission>();
    public DbSet<RolePermission> RolePermissions => Set<RolePermission>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OtpCode> OtpCodes => Set<OtpCode>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<BdLocation> BdLocations => Set<BdLocation>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        ConfigureUser(modelBuilder.Entity<User>());
        ConfigureAddress(modelBuilder.Entity<Address>());
        ConfigureRole(modelBuilder.Entity<Role>());
        ConfigurePermission(modelBuilder.Entity<Permission>());
        ConfigureRolePermission(modelBuilder.Entity<RolePermission>());
        ConfigureUserRole(modelBuilder.Entity<UserRole>());
        ConfigureRefreshToken(modelBuilder.Entity<RefreshToken>());
        ConfigureOtpCode(modelBuilder.Entity<OtpCode>());
        ConfigureAuditLog(modelBuilder.Entity<AuditLog>());
        ConfigureBdLocation(modelBuilder.Entity<BdLocation>());
    }

    private static void ConfigureUser(EntityTypeBuilder<User> builder)
    {
        builder.ToTable("users");
        builder.HasKey(u => u.Id);
        builder.Property(u => u.Id).HasColumnName("id");
        builder.Property(u => u.Email).HasColumnName("email").HasMaxLength(255).IsRequired();
        builder.Property(u => u.Phone).HasColumnName("phone").HasMaxLength(20);
        builder.Property(u => u.PasswordHash).HasColumnName("password_hash");
        builder.Property(u => u.FullName).HasColumnName("full_name").HasMaxLength(150).IsRequired();
        builder.Property(u => u.AvatarUrl).HasColumnName("avatar_url");
        builder.Property(u => u.Locale).HasColumnName("locale").HasMaxLength(5).HasDefaultValue("en");
        builder.Property(u => u.Status).HasColumnName("status").HasConversion<string>().HasMaxLength(30);
        builder.Property(u => u.TwoFactorEnabled).HasColumnName("two_factor_enabled");
        builder.Property(u => u.TwoFactorSecret).HasColumnName("two_factor_secret");
        builder.Property(u => u.TwoFactorRecoveryCodesJson).HasColumnName("two_factor_recovery_codes_json");
        builder.Property(u => u.LastUsedTotpTimeStep).HasColumnName("last_used_totp_time_step");
        builder.Property(u => u.GoogleSubjectId).HasColumnName("google_subject_id").HasMaxLength(128);
        builder.Property(u => u.SellerId).HasColumnName("seller_id");
        builder.Property(u => u.FailedLoginAttempts).HasColumnName("failed_login_attempts");
        builder.Property(u => u.LockoutUntilUtc).HasColumnName("lockout_until");
        builder.Property(u => u.EmailVerifiedAtUtc).HasColumnName("email_verified_at");
        builder.Property(u => u.PhoneVerifiedAtUtc).HasColumnName("phone_verified_at");
        builder.Property(u => u.LastLoginAtUtc).HasColumnName("last_login_at");
        builder.Property(u => u.CreatedAtUtc).HasColumnName("created_at");
        builder.Property(u => u.UpdatedAtUtc).HasColumnName("updated_at");
        builder.Property(u => u.DeletedAtUtc).HasColumnName("deleted_at");

        builder.HasIndex(u => u.Email).IsUnique().HasFilter("deleted_at IS NULL");
        builder.HasIndex(u => u.Phone).IsUnique().HasFilter("phone IS NOT NULL AND deleted_at IS NULL");
        builder.HasIndex(u => u.GoogleSubjectId).IsUnique().HasFilter("google_subject_id IS NOT NULL");

        builder.Ignore(u => u.DomainEvents);
    }

    private static void ConfigureAddress(EntityTypeBuilder<Address> builder)
    {
        builder.ToTable("addresses");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(a => a.Label).HasColumnName("label").HasMaxLength(50).IsRequired();
        builder.Property(a => a.RecipientName).HasColumnName("recipient_name").HasMaxLength(150).IsRequired();
        builder.Property(a => a.Phone).HasColumnName("phone").HasMaxLength(20).IsRequired();
        builder.Property(a => a.Line1).HasColumnName("line1").HasMaxLength(255).IsRequired();
        builder.Property(a => a.Line2).HasColumnName("line2").HasMaxLength(255);
        builder.Property(a => a.Area).HasColumnName("area").HasMaxLength(100);
        builder.Property(a => a.Upazila).HasColumnName("upazila").HasMaxLength(100).IsRequired();
        builder.Property(a => a.District).HasColumnName("district").HasMaxLength(100).IsRequired();
        builder.Property(a => a.Division).HasColumnName("division").HasMaxLength(100).IsRequired();
        builder.Property(a => a.PostalCode).HasColumnName("postal_code").HasMaxLength(20);
        builder.Property(a => a.Country).HasColumnName("country").HasMaxLength(2).HasDefaultValue("BD");
        builder.Property(a => a.IsDefaultShipping).HasColumnName("is_default_shipping");
        builder.Property(a => a.IsDefaultBilling).HasColumnName("is_default_billing");
        builder.Property(a => a.Latitude).HasColumnName("latitude").HasPrecision(10, 7);
        builder.Property(a => a.Longitude).HasColumnName("longitude").HasPrecision(10, 7);
        builder.Property(a => a.CreatedAtUtc).HasColumnName("created_at");
        builder.Property(a => a.UpdatedAtUtc).HasColumnName("updated_at");

        builder.HasIndex(a => a.UserId);
    }

    private static void ConfigureRole(EntityTypeBuilder<Role> builder)
    {
        builder.ToTable("roles");
        builder.HasKey(r => r.Id);
        builder.Property(r => r.Id).HasColumnName("id");
        builder.Property(r => r.Name).HasColumnName("name").HasMaxLength(50).IsRequired();
        builder.Property(r => r.Description).HasColumnName("description");
        builder.Property(r => r.IsSystem).HasColumnName("is_system");
        builder.HasIndex(r => r.Name).IsUnique();
    }

    private static void ConfigurePermission(EntityTypeBuilder<Permission> builder)
    {
        builder.ToTable("permissions");
        builder.HasKey(p => p.Id);
        builder.Property(p => p.Id).HasColumnName("id");
        builder.Property(p => p.Code).HasColumnName("code").HasMaxLength(100).IsRequired();
        builder.Property(p => p.Module).HasColumnName("module").HasMaxLength(50).IsRequired();
        builder.Property(p => p.Description).HasColumnName("description");
        builder.HasIndex(p => p.Code).IsUnique();
    }

    private static void ConfigureRolePermission(EntityTypeBuilder<RolePermission> builder)
    {
        builder.ToTable("role_permissions");
        builder.HasKey(rp => new { rp.RoleId, rp.PermissionId });
        builder.Property(rp => rp.RoleId).HasColumnName("role_id");
        builder.Property(rp => rp.PermissionId).HasColumnName("permission_id");
    }

    private static void ConfigureUserRole(EntityTypeBuilder<UserRole> builder)
    {
        builder.ToTable("user_roles");
        builder.HasKey(ur => new { ur.UserId, ur.RoleId });
        builder.Property(ur => ur.UserId).HasColumnName("user_id");
        builder.Property(ur => ur.RoleId).HasColumnName("role_id");
        builder.Property(ur => ur.RoleName).HasColumnName("role_name").HasMaxLength(50);
        builder.Property(ur => ur.AssignedAtUtc).HasColumnName("assigned_at");
    }

    private static void ConfigureRefreshToken(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("refresh_tokens");
        builder.HasKey(rt => rt.Id);
        builder.Property(rt => rt.Id).HasColumnName("id");
        builder.Property(rt => rt.UserId).HasColumnName("user_id").IsRequired();
        builder.Property(rt => rt.TokenHash).HasColumnName("token_hash").HasMaxLength(128).IsRequired();
        builder.Property(rt => rt.FamilyId).HasColumnName("family_id").IsRequired();
        builder.Property(rt => rt.IsUsed).HasColumnName("is_used");
        builder.Property(rt => rt.IsRevoked).HasColumnName("is_revoked");
        builder.Property(rt => rt.UsedAtUtc).HasColumnName("used_at");
        builder.Property(rt => rt.ReplacedByTokenHash).HasColumnName("replaced_by_token_hash").HasMaxLength(128);
        builder.Property(rt => rt.CreatedByIp).HasColumnName("created_by_ip").HasMaxLength(45);
        builder.Property(rt => rt.UserAgent).HasColumnName("user_agent");
        builder.Property(rt => rt.ExpiresAtUtc).HasColumnName("expires_at");
        builder.Property(rt => rt.CreatedAtUtc).HasColumnName("created_at");
        builder.Property(rt => rt.UpdatedAtUtc).HasColumnName("updated_at");

        builder.HasIndex(rt => rt.TokenHash).IsUnique();
        builder.HasIndex(rt => rt.FamilyId);
        builder.HasIndex(rt => rt.UserId);
    }

    private static void ConfigureOtpCode(EntityTypeBuilder<OtpCode> builder)
    {
        builder.ToTable("otp_codes");
        builder.HasKey(o => o.Id);
        builder.Property(o => o.Id).HasColumnName("id");
        builder.Property(o => o.Recipient).HasColumnName("recipient").HasMaxLength(255).IsRequired();
        builder.Property(o => o.CodeHash).HasColumnName("code_hash").HasMaxLength(128).IsRequired();
        builder.Property(o => o.Type).HasColumnName("type").HasConversion<string>().HasMaxLength(30).IsRequired();
        builder.Property(o => o.ReferenceId).HasColumnName("reference_id").HasMaxLength(64).IsRequired();
        builder.Property(o => o.Attempts).HasColumnName("attempts");
        builder.Property(o => o.IsConsumed).HasColumnName("is_consumed");
        builder.Property(o => o.ExpiresAtUtc).HasColumnName("expires_at");
        builder.Property(o => o.CreatedAtUtc).HasColumnName("created_at");

        builder.HasIndex(o => new { o.Recipient, o.Type, o.ReferenceId });
    }

    private static void ConfigureAuditLog(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("audit_logs");
        builder.HasKey(a => a.Id);
        builder.Property(a => a.Id).HasColumnName("id");
        builder.Property(a => a.ActorId).HasColumnName("actor_id");
        builder.Property(a => a.ActorIp).HasColumnName("actor_ip").HasMaxLength(45);
        builder.Property(a => a.Action).HasColumnName("action").HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityName).HasColumnName("entity_name").HasMaxLength(100).IsRequired();
        builder.Property(a => a.EntityId).HasColumnName("entity_id").HasMaxLength(100).IsRequired();
        builder.Property(a => a.OldValuesJson).HasColumnName("old_values").HasColumnType("jsonb");
        builder.Property(a => a.NewValuesJson).HasColumnName("new_values").HasColumnType("jsonb");
        builder.Property(a => a.CorrelationId).HasColumnName("correlation_id").HasMaxLength(100);
        builder.Property(a => a.CreatedAtUtc).HasColumnName("created_at");

        builder.HasIndex(a => new { a.EntityName, a.EntityId });
        builder.HasIndex(a => new { a.ActorId, a.CreatedAtUtc });
    }

    private static void ConfigureBdLocation(EntityTypeBuilder<BdLocation> builder)
    {
        builder.ToTable("bd_locations");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.Id).HasColumnName("id");
        builder.Property(l => l.ParentId).HasColumnName("parent_id");
        builder.Property(l => l.Level).HasColumnName("level").HasMaxLength(20).IsRequired();
        builder.Property(l => l.NameEn).HasColumnName("name_en").HasMaxLength(100).IsRequired();
        builder.Property(l => l.NameBn).HasColumnName("name_bn").HasMaxLength(100).IsRequired();
        builder.Property(l => l.ShippingZone).HasColumnName("shipping_zone").HasMaxLength(30).IsRequired();
        builder.Property(l => l.PostalCodePrefix).HasColumnName("postal_code_prefix").HasMaxLength(10);

        builder.HasIndex(l => new { l.Level, l.ParentId });
    }
}
