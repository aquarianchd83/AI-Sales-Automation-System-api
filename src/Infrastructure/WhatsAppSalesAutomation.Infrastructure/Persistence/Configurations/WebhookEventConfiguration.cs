using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Webhooks;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class WebhookEventConfiguration : IEntityTypeConfiguration<WebhookEvent>
{
    public void Configure(EntityTypeBuilder<WebhookEvent> builder)
    {
        builder.ToTable("WebhookEvents");
        builder.HasKey(w => w.Id);

        builder.Property(w => w.Provider).IsRequired().HasMaxLength(50);
        builder.Property(w => w.EventType).IsRequired().HasMaxLength(50);
        builder.Property(w => w.RawPayload).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(w => w.WhatsAppMessageId).HasMaxLength(100);
        builder.Property(w => w.ProcessingStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(w => w.ProcessingError).HasMaxLength(2000);

        // Non-unique: Meta's own redeliveries are exactly why this exists - a genuine duplicate
        // WhatsAppMessageId is expected, not an error, and is how ProcessingStatus.Duplicate gets set.
        builder.HasIndex(w => w.WhatsAppMessageId);
        builder.HasIndex(w => w.ProcessingStatus);
    }
}
