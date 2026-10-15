using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class CustomerHistoryEventConfiguration : IEntityTypeConfiguration<CustomerHistoryEvent>
{
    public void Configure(EntityTypeBuilder<CustomerHistoryEvent> builder)
    {
        builder.ToTable("CustomerHistoryEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventType).HasMaxLength(50).IsRequired();
        builder.Property(e => e.PreviousStatus).HasMaxLength(50);
        builder.Property(e => e.NewStatus).HasMaxLength(50);
        builder.Property(e => e.Reason).HasMaxLength(1000);
        builder.Property(e => e.CorrelationId).HasMaxLength(100);
        builder.Property(e => e.ProviderReference).HasMaxLength(200);

        builder.HasIndex(e => new { e.TenantId, e.CustomerId, e.OccurredAtUtc });
    }
}
