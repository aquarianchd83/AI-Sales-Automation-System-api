using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AiProviderModelConfiguration : IEntityTypeConfiguration<AiProviderModel>
{
    public void Configure(EntityTypeBuilder<AiProviderModel> builder)
    {
        builder.ToTable("AiProviderModels");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Provider).IsRequired().HasMaxLength(50);
        builder.Property(m => m.DisplayName).IsRequired().HasMaxLength(200);
        builder.Property(m => m.ModelId).IsRequired().HasMaxLength(200);
        builder.Property(m => m.ModelType).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.CostTier).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.BestFor).IsRequired().HasMaxLength(500);
        builder.Property(m => m.InputPricePer1MUsd).HasPrecision(18, 6);
        builder.Property(m => m.OutputPricePer1MUsd).HasPrecision(18, 6);
        builder.Property(m => m.Description).HasMaxLength(1000);

        builder.HasIndex(m => new { m.Provider, m.ModelId }).IsUnique();
        builder.HasIndex(m => m.IsActive);
    }
}
