using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class ConsentInvitationConfiguration : IEntityTypeConfiguration<ConsentInvitation>
{
    public void Configure(EntityTypeBuilder<ConsentInvitation> builder)
    {
        builder.ToTable("ConsentInvitations");
        builder.HasKey(i => i.Id);

        builder.Property(i => i.IdempotencyKey).IsRequired().HasMaxLength(120);
        builder.Property(i => i.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(i => i.ToPhoneE164).IsRequired().HasMaxLength(20);
        builder.Property(i => i.Body).IsRequired().HasMaxLength(1000);
        builder.Property(i => i.WhatsAppLink).IsRequired().HasMaxLength(200);
        builder.Property(i => i.ProviderMessageId).HasMaxLength(200);
        builder.Property(i => i.FailureReason).HasMaxLength(1000);
        builder.Property(i => i.ConsentMessageId).HasMaxLength(200);
        builder.Property(i => i.CorrelationId).HasMaxLength(100);

        // The final guarantee that one customer is invited once per campaign, however many retries, reruns or racing instances there are.
        builder.HasIndex(i => i.IdempotencyKey).IsUnique();
        builder.HasIndex(i => new { i.TenantId, i.CustomerId });
        builder.HasIndex(i => i.ProviderMessageId);
        builder.HasIndex(i => new { i.Status, i.NextAttemptAt });
    }
}
