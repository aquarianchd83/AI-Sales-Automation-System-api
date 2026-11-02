using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Enums;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class NextStepBookingConfiguration : IEntityTypeConfiguration<NextStepBooking>
{
    public void Configure(EntityTypeBuilder<NextStepBooking> builder)
    {
        builder.ToTable("NextStepBookings");
        builder.HasKey(b => b.Id);
        builder.Property(b => b.Kind).IsRequired().HasMaxLength(30);
        builder.Property(b => b.Summary).IsRequired().HasMaxLength(400);
        builder.Property(b => b.RescheduleReason).HasMaxLength(500);
        builder.Property(b => b.CustomerEmail).HasMaxLength(254);
        builder.Property(b => b.ConfirmationStatus).HasMaxLength(20);
        builder.Property(b => b.ConfirmationNote).HasMaxLength(300);
        builder.Property(b => b.Status).HasConversion<string>().HasMaxLength(20).HasDefaultValue(BookingStatus.Pending);

        // The list is the tenant's bookings, newest first.
        builder.HasIndex(b => new { b.TenantId, b.CreatedAt });
        // "Is this the same booking again?" is asked per conversation.
        builder.HasIndex(b => new { b.TenantId, b.ConversationId });
    }
}
