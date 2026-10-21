using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlatformTicketConfiguration : IEntityTypeConfiguration<PlatformTicket>
{
    public void Configure(EntityTypeBuilder<PlatformTicket> builder)
    {
        builder.ToTable("PlatformTickets");
        builder.HasKey(t => t.Id);
        builder.Property(t => t.Kind).HasConversion<string>().HasMaxLength(40);
        builder.Property(t => t.DedupeKey).IsRequired().HasMaxLength(150);
        builder.Property(t => t.Subject).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Description).IsRequired().HasMaxLength(2000);
        builder.Property(t => t.Priority).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.DeliveryNote).HasMaxLength(500);

        builder.HasIndex(t => new { t.Status, t.Priority, t.CreatedAt });
        builder.HasIndex(t => t.DedupeKey);
    }
}
