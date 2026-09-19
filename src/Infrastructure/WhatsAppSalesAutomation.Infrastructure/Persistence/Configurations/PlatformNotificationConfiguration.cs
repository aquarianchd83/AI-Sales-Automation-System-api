using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlatformNotificationConfiguration : IEntityTypeConfiguration<PlatformNotification>
{
    public void Configure(EntityTypeBuilder<PlatformNotification> builder)
    {
        builder.ToTable("PlatformNotifications");
        builder.HasKey(n => n.Id);
        builder.Property(n => n.Kind).HasConversion<string>().HasMaxLength(30);
        builder.Property(n => n.Severity).HasConversion<string>().HasMaxLength(20);
        builder.Property(n => n.JobType).HasMaxLength(100);
        builder.Property(n => n.EpisodeKey).IsRequired().HasMaxLength(100);
        builder.Property(n => n.Title).IsRequired().HasMaxLength(200);
        builder.Property(n => n.Body).IsRequired().HasMaxLength(1000);
        builder.Property(n => n.EmailStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(n => n.DeliveryNote).HasMaxLength(500);

        // One alert per (kind, tenant, job, episode), so a re-run of the same failure streak never alerts twice.
        builder.HasIndex(n => new { n.Kind, n.TenantId, n.JobType, n.EpisodeKey }).IsUnique();
        builder.HasIndex(n => n.CreatedAt);
    }
}
