using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class TenantOnboardingStepConfiguration : IEntityTypeConfiguration<TenantOnboardingStep>
{
    public void Configure(EntityTypeBuilder<TenantOnboardingStep> builder)
    {
        builder.ToTable("TenantOnboardingSteps");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.StepKey).IsRequired().HasMaxLength(40);

        // A step is completed once per tenant.
        builder.HasIndex(s => new { s.TenantId, s.StepKey }).IsUnique();
    }
}
