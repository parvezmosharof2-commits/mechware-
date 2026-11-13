using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Domain.Identity;
using Mechware.Domain.Shipping;

namespace Mechware.Infrastructure.Persistence.Configurations;

internal sealed class ShippingZoneConfiguration : IEntityTypeConfiguration<ShippingZone>
{
    public void Configure(EntityTypeBuilder<ShippingZone> b)
    {
        b.ToTable("shipping_zones", t =>
        {
            t.HasCheckConstraint("ck_shipping_zones_max_fee", "max_fee_minor > 0");
            t.HasCheckConstraint("ck_shipping_zones_free_threshold", "free_shipping_threshold_minor IS NULL OR free_shipping_threshold_minor > 0");
        });
        b.MapAudit();
        b.Property(z => z.Code).HasColumnName("code").HasMaxLength(40).IsRequired();
        b.Property(z => z.NameEn).HasColumnName("name_en").HasMaxLength(100).IsRequired();
        b.Property(z => z.NameBn).HasColumnName("name_bn").HasMaxLength(100).IsRequired();
        b.Property(z => z.IsActive).HasColumnName("is_active");
        b.Property(z => z.FreeShippingThresholdMinor).HasColumnName("free_shipping_threshold_minor");
        b.Property(z => z.HeavyUnitGrams).HasColumnName("heavy_unit_grams");
        b.Property(z => z.OversizeDimensionMm).HasColumnName("oversize_dimension_mm");
        b.Property(z => z.MaxFeeMinor).HasColumnName("max_fee_minor");
        b.HasIndex(z => z.Code).IsUnique().HasDatabaseName("ux_shipping_zones_code");

        b.HasMany(z => z.Districts).WithOne().HasForeignKey(d => d.ZoneId).OnDelete(DeleteBehavior.Cascade);
        b.HasMany(z => z.Bands).WithOne().HasForeignKey(x => x.ZoneId).OnDelete(DeleteBehavior.Cascade);
        b.Navigation(z => z.Districts).UsePropertyAccessMode(PropertyAccessMode.Field);
        b.Navigation(z => z.Bands).UsePropertyAccessMode(PropertyAccessMode.Field);
    }
}

internal sealed class ShippingZoneDistrictConfiguration : IEntityTypeConfiguration<ShippingZoneDistrict>
{
    public void Configure(EntityTypeBuilder<ShippingZoneDistrict> b)
    {
        b.ToTable("shipping_zone_districts");
        // The key is the district alone: a district can be in only one zone, enforced by the database.
        b.HasKey(d => d.DistrictId);
        b.Property(d => d.DistrictId).HasColumnName("district_id").ValueGeneratedNever();
        b.Property(d => d.ZoneId).HasColumnName("zone_id");
        b.HasOne<BdLocation>().WithMany().HasForeignKey(d => d.DistrictId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(d => d.ZoneId).HasDatabaseName("ix_shipping_zone_districts_zone");
    }
}

internal sealed class ShippingRateBandConfiguration : IEntityTypeConfiguration<ShippingRateBand>
{
    public void Configure(EntityTypeBuilder<ShippingRateBand> b)
    {
        b.ToTable("shipping_rate_bands", t =>
        {
            t.HasCheckConstraint("ck_shipping_rate_bands_grams", "max_grams > 0");
            t.HasCheckConstraint("ck_shipping_rate_bands_fee", "fee_minor >= 0");
        });
        b.HasKey(x => new { x.ZoneId, x.MaxGrams });
        b.Property(x => x.ZoneId).HasColumnName("zone_id");
        b.Property(x => x.MaxGrams).HasColumnName("max_grams");
        b.Property(x => x.FeeMinor).HasColumnName("fee_minor");
    }
}
