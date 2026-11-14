using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class DatabaseCleanupLogConfiguration : IEntityTypeConfiguration<DatabaseCleanupLog>
{
    public void Configure(EntityTypeBuilder<DatabaseCleanupLog> builder)
    {
        builder.ToTable("DatabaseCleanupLogs");
        builder.HasKey(l => l.Id);
        builder.Property(l => l.TableKey).IsRequired().HasMaxLength(60);

        builder.HasIndex(l => l.CreatedAt);
        builder.HasIndex(l => l.RunId);
    }
}
