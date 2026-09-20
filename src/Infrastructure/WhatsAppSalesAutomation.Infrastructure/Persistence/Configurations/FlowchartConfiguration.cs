using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class FlowchartConfiguration : IEntityTypeConfiguration<Flowchart>
{
    public void Configure(EntityTypeBuilder<Flowchart> builder)
    {
        builder.ToTable("Flowcharts");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Title).IsRequired().HasMaxLength(200);
        builder.Property(f => f.Description).HasMaxLength(2000);
        builder.Property(f => f.Category).HasMaxLength(100);
        builder.Property(f => f.DiagramJson).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(f => f.Status).HasConversion<string>().HasMaxLength(20);

        builder.HasIndex(f => f.Status);
    }
}
