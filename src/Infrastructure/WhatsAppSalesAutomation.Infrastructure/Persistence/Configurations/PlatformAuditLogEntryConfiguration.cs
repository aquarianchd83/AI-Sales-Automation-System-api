using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlatformAuditLogEntryConfiguration : IEntityTypeConfiguration<PlatformAuditLogEntry>
{
    public void Configure(EntityTypeBuilder<PlatformAuditLogEntry> builder)
    {
        builder.ToTable("PlatformAuditLogEntries");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.ActorEmail).IsRequired().HasMaxLength(256);
        builder.Property(a => a.Action).IsRequired().HasMaxLength(100);
        builder.Property(a => a.Details).HasMaxLength(4000);

        builder.HasIndex(a => a.Action);
        builder.HasIndex(a => a.TargetTenantId);
        builder.HasIndex(a => a.CreatedAt);
    }
}
