using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class LeadDiscoveryScheduleSlotConfiguration : IEntityTypeConfiguration<LeadDiscoveryScheduleSlot>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryScheduleSlot> builder)
    {
        builder.ToTable("LeadDiscoveryScheduleSlots");
        builder.HasKey(s => s.Id);
        builder.HasIndex(s => new { s.IsDone, s.NextRunAtUtc });
        builder.HasIndex(s => s.TenantId);
    }
}
