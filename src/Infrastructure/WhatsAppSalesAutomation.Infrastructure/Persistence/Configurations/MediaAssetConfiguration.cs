using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Media;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class MediaAssetConfiguration : IEntityTypeConfiguration<MediaAsset>
{
    public void Configure(EntityTypeBuilder<MediaAsset> builder)
    {
        builder.ToTable("MediaAssets");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.FileName).IsRequired().HasMaxLength(260);
        builder.Property(m => m.ContentType).IsRequired().HasMaxLength(100);
        builder.Property(m => m.StorageProvider).IsRequired().HasMaxLength(50);
        builder.Property(m => m.StorageKey).IsRequired().HasMaxLength(500);
        builder.Property(m => m.Url).IsRequired().HasMaxLength(1000);
        builder.Property(m => m.Checksum).IsRequired().HasMaxLength(64);
        builder.Property(m => m.WhatsAppMediaId).HasMaxLength(200);

        // Not unique: two different customers' logos could legitimately share bytes in theory, but
        // the practical use is dedup lookup on upload, which does not need uniqueness enforced.
        builder.HasIndex(m => m.Checksum);
    }
}
