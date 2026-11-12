using Microsoft.AspNetCore.DataProtection.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mechware.Infrastructure.Persistence.Configurations;

/// <summary>ASP.NET Core Data Protection key ring (see Security.DataProtectionKeyRing). Never audited: the XML is key material.</summary>
internal sealed class DataProtectionKeyConfiguration : IEntityTypeConfiguration<DataProtectionKey>
{
    public void Configure(EntityTypeBuilder<DataProtectionKey> b)
    {
        b.ToTable("data_protection_keys");
        b.HasKey(k => k.Id);
        b.Property(k => k.Id).HasColumnName("id");
        b.Property(k => k.FriendlyName).HasColumnName("friendly_name");
        b.Property(k => k.Xml).HasColumnName("xml");
    }
}
