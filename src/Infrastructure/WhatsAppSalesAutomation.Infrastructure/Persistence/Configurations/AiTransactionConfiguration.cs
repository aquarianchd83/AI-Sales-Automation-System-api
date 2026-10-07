using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Ai;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AiTransactionConfiguration : IEntityTypeConfiguration<AiTransaction>
{
    public void Configure(EntityTypeBuilder<AiTransaction> builder)
    {
        builder.ToTable("AiTransactions");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Operation).IsRequired().HasMaxLength(50);
        builder.Property(t => t.Source).HasConversion<string>().HasMaxLength(10);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(15);
        builder.Property(t => t.DenialReason).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.Provider).IsRequired().HasMaxLength(30);
        builder.Property(t => t.Model).IsRequired().HasMaxLength(100);
        builder.Property(t => t.OperationKey).IsRequired().HasMaxLength(100);
        builder.Property(t => t.ReferenceId).IsRequired().HasMaxLength(100);
        builder.Property(t => t.FailureReason).HasMaxLength(500);
        builder.Property(t => t.CreditsBefore).HasColumnType("decimal(18,4)");
        builder.Property(t => t.CreditsConsumed).HasColumnType("decimal(18,4)");
        builder.Property(t => t.CreditsAfter).HasColumnType("decimal(18,4)");
        builder.Property(t => t.CreditsRefunded).HasColumnType("decimal(18,4)");

        builder.HasIndex(t => new { t.TenantId, t.RequestedAtUtc });
        // The retry lookup: has this operation already been authorised for this reference?
        builder.HasIndex(t => new { t.TenantId, t.Operation, t.ReferenceId });
    }
}
