using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class SimulatorDummyRecordConfiguration : IEntityTypeConfiguration<SimulatorDummyRecord>
{
    public void Configure(EntityTypeBuilder<SimulatorDummyRecord> builder)
    {
        builder.ToTable("SimulatorDummyRecords");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Category).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.Name).IsRequired().HasMaxLength(200);
        builder.Property(r => r.Value).HasMaxLength(4000);
        builder.Property(r => r.BusinessType).HasMaxLength(300);
        builder.Property(r => r.ContactPerson).HasMaxLength(300);
        builder.Property(r => r.Address).HasMaxLength(300);
        builder.Property(r => r.City).HasMaxLength(300);
        builder.Property(r => r.Phone).HasMaxLength(40);
        builder.Property(r => r.Email).HasMaxLength(300);
        builder.Property(r => r.Website).HasMaxLength(300);
        builder.Property(r => r.Variant).HasConversion<string>().HasMaxLength(30);

        builder.HasIndex(r => new { r.Category, r.DisplayOrder });
    }
}
