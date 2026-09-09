using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Infrastructure.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class TenantAiProviderConfigConfiguration : IEntityTypeConfiguration<TenantAiProviderConfig>
{
    public void Configure(EntityTypeBuilder<TenantAiProviderConfig> builder)
    {
        builder.ToTable("TenantAiProviderConfigs");
        builder.HasKey(c => c.TenantId);

        builder.Property(c => c.Provider).IsRequired().HasMaxLength(20);
        builder.Property(c => c.EmbeddingProvider).IsRequired().HasMaxLength(20);

        // No HasQueryFilter here - see TenantWhatsAppConfigConfiguration's equivalent comment.
    }
}
