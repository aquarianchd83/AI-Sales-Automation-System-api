using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class TenantWhatsAppConfigConfiguration : IEntityTypeConfiguration<TenantWhatsAppConfig>
{
    public void Configure(EntityTypeBuilder<TenantWhatsAppConfig> builder)
    {
        builder.ToTable("TenantWhatsAppConfigs");
        builder.HasKey(c => c.TenantId);

        builder.Property(c => c.PhoneNumberId).IsRequired().HasMaxLength(64);
        builder.Property(c => c.WhatsAppBusinessAccountId).HasMaxLength(64);
        builder.Property(c => c.AppId).HasMaxLength(64);
        builder.Property(c => c.ApiVersion).HasMaxLength(20);
        builder.Property(c => c.ApiBaseUrl).HasMaxLength(500);

        // The webhook routing key (WebhooksController.Receive resolves the inbound tenant off Meta's
        // metadata.phone_number_id before anything else) - one tenant's WABA phone number can never
        // legitimately be reused by another tenant's config.
        builder.HasIndex(c => c.PhoneNumberId).IsUnique();

        // No HasQueryFilter here - TenantWhatsAppConfig is ITenantOwned, so the combined filter is
        // built reflectively in ApplicationDbContext.OnModelCreating, same as every other ITenantOwned
        // entity (see Customer/KnowledgeBaseArticle's equivalent comment).
    }
}
