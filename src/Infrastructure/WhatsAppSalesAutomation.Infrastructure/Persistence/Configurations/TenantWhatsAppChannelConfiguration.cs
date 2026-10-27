using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class TenantWhatsAppChannelConfiguration : IEntityTypeConfiguration<TenantWhatsAppChannel>
{
    public void Configure(EntityTypeBuilder<TenantWhatsAppChannel> builder)
    {
        builder.ToTable("TenantWhatsAppChannels");
        builder.HasKey(c => c.TenantId);

        builder.Property(c => c.InstanceName).IsRequired().HasMaxLength(64);
        builder.Property(c => c.InstanceTokenCipher).IsRequired().HasMaxLength(1000);
        builder.Property(c => c.WebhookTokenHash).IsRequired().HasMaxLength(64);
        builder.Property(c => c.WebhookSecretHash).IsRequired().HasMaxLength(64);
        builder.Property(c => c.Status).IsRequired().HasMaxLength(32);
        builder.Property(c => c.ConnectedPhone).HasMaxLength(32);
        builder.Property(c => c.LastError).HasMaxLength(300);

        // A bridge instance belongs to exactly one tenant, and the webhook route key is looked up by its hash.
        builder.HasIndex(c => c.InstanceName).IsUnique();
        builder.HasIndex(c => c.WebhookTokenHash).IsUnique();
    }
}
