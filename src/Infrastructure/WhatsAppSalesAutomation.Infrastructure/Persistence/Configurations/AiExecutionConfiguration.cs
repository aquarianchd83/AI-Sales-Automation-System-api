using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AiExecutionConfiguration : IEntityTypeConfiguration<AiExecution>
{
    public void Configure(EntityTypeBuilder<AiExecution> builder)
    {
        builder.ToTable("AiExecutions");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.Task).IsRequired().HasMaxLength(60);
        builder.Property(e => e.Feature).HasMaxLength(60);
        builder.Property(e => e.RequestedProvider).IsRequired().HasMaxLength(20);
        builder.Property(e => e.Provider).IsRequired().HasMaxLength(20);
        builder.Property(e => e.Model).HasMaxLength(100);
        builder.Property(e => e.Mode).HasConversion<string>().HasMaxLength(10);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(10);
        builder.Property(e => e.FallbackFromProvider).HasMaxLength(20);
        builder.Property(e => e.EstimatedCostUsd).HasPrecision(18, 6);
        builder.Property(e => e.ErrorCode).HasMaxLength(60);
        builder.Property(e => e.ErrorSummary).HasMaxLength(300);
        builder.Property(e => e.Detail).HasMaxLength(4000);

        builder.HasIndex(e => e.CreatedAt);
        builder.HasIndex(e => new { e.Provider, e.Task, e.Status, e.CreatedAt });
        builder.HasIndex(e => e.CorrelationId);
    }
}
