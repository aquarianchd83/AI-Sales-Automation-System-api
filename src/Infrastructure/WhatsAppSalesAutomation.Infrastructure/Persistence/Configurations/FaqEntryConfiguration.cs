using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class FaqEntryConfiguration : IEntityTypeConfiguration<FaqEntry>
{
    public void Configure(EntityTypeBuilder<FaqEntry> builder)
    {
        builder.ToTable("FaqEntries");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Question).IsRequired().HasMaxLength(500);
        builder.Property(f => f.Answer).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(f => f.Category).HasMaxLength(100);
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(f => f.Status);
    }
}
