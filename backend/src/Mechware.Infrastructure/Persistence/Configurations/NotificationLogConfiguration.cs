using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Mechware.Infrastructure.Persistence.Outbox;

namespace Mechware.Infrastructure.Persistence.Configurations;

internal sealed class NotificationLogConfiguration : IEntityTypeConfiguration<NotificationLogEntry>
{
    public void Configure(EntityTypeBuilder<NotificationLogEntry> b)
    {
        b.ToTable("notification_log");
        b.HasKey(n => n.Id);
        b.Property(n => n.Id).HasColumnName("id").ValueGeneratedNever();
        b.Property(n => n.EventId).HasColumnName("event_id");
        b.Property(n => n.RecipientUserId).HasColumnName("recipient_user_id");
        b.Property(n => n.TemplateKey).HasColumnName("template_key").HasMaxLength(80).IsRequired();
        b.Property(n => n.Status).HasColumnName("status").HasMaxLength(16).IsRequired();
        b.Property(n => n.Error).HasColumnName("error").HasMaxLength(500);
        b.Property(n => n.CreatedAtUtc).HasColumnName("created_at");
        b.Property(n => n.UpdatedAtUtc).HasColumnName("updated_at");
        b.HasIndex(n => new { n.EventId, n.RecipientUserId, n.TemplateKey }).IsUnique().HasDatabaseName("ux_notification_log_event_recipient_template");
    }
}
