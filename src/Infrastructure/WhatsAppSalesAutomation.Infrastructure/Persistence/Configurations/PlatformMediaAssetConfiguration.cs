using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlatformMediaAssetConfiguration : IEntityTypeConfiguration<PlatformMediaAsset>
{
    public void Configure(EntityTypeBuilder<PlatformMediaAsset> builder)
    {
        builder.ToTable("PlatformMediaAssets");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.FileName).IsRequired().HasMaxLength(260);
        builder.Property(m => m.ContentType).IsRequired().HasMaxLength(100);
        builder.Property(m => m.StorageProvider).IsRequired().HasMaxLength(50);
        builder.Property(m => m.StorageKey).IsRequired().HasMaxLength(500);
        builder.Property(m => m.ThumbnailStorageKey).HasMaxLength(500);
        builder.Property(m => m.Url).IsRequired().HasMaxLength(1000);
        builder.Property(m => m.Checksum).IsRequired().HasMaxLength(64);

        builder.HasIndex(m => m.Checksum);
    }
}
