using Mechware.Domain.Identity;
using Mechware.Infrastructure.Persistence.Outbox;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace Mechware.Infrastructure.Persistence.Configurations;

internal sealed class InAppNotificationConfiguration : IEntityTypeConfiguration<InAppNotification>
{
    public void Configure(EntityTypeBuilder<InAppNotification> b)
    {
        // A relative web path: starts with one "/", not "//" (protocol-relative), no backslash, no control characters.
        b.ToTable("in_app_notifications", t => t.HasCheckConstraint("ck_in_app_notifications_link_relative",
            "link_path LIKE '/%' AND link_path NOT LIKE '//%' AND position(chr(92) in link_path) = 0 AND link_path !~ '[[:cntrl:]]'"));
        b.HasKey(n => n.Id);
        b.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(n => n.UserId).HasColumnName("user_id");
        b.Property(n => n.EventId).HasColumnName("event_id");
        b.Property(n => n.Kind).HasColumnName("kind").HasMaxLength(60).IsRequired();
        b.Property(n => n.TitleKey).HasColumnName("title_key").HasMaxLength(100).IsRequired();
        b.Property(n => n.ParamsJson).HasColumnName("params").HasColumnType("jsonb").IsRequired();
        b.Property(n => n.LinkPath).HasColumnName("link_path").HasMaxLength(400).IsRequired();
        b.Property(n => n.CreatedAtUtc).HasColumnName("created_at");
        b.Property(n => n.ReadAtUtc).HasColumnName("read_at");
        b.HasOne<User>().WithMany().HasForeignKey(n => n.UserId).OnDelete(DeleteBehavior.Cascade);
        b.HasIndex(n => new { n.EventId, n.UserId }).IsUnique().HasDatabaseName("ux_in_app_notifications_event_user");
        b.HasIndex(n => new { n.UserId, n.ReadAtUtc, n.CreatedAtUtc }).HasDatabaseName("ix_in_app_notifications_user_read_created");
    }
}
