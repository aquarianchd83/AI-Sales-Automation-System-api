using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Payments;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class RazorpayOrderConfiguration : IEntityTypeConfiguration<RazorpayOrder>
{
    public void Configure(EntityTypeBuilder<RazorpayOrder> builder)
    {
        builder.ToTable("RazorpayOrders");
        builder.HasKey(o => o.Id);

        builder.Property(o => o.OrderId).IsRequired().HasMaxLength(40);
        builder.Property(o => o.Receipt).IsRequired().HasMaxLength(40);
        builder.Property(o => o.Currency).IsRequired().HasMaxLength(3);
        builder.Property(o => o.Description).IsRequired().HasMaxLength(255);
        builder.Property(o => o.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(o => o.Mode).IsRequired().HasMaxLength(4);
        builder.Property(o => o.PaymentId).HasMaxLength(40);
        builder.Property(o => o.Method).HasMaxLength(30);
        builder.Property(o => o.LastRefundId).HasMaxLength(40);
        builder.Property(o => o.FailureReason).HasMaxLength(500);
        builder.Property(o => o.LastWebhookEvent).HasMaxLength(60);

        builder.HasIndex(o => o.OrderId).IsUnique();
        builder.HasIndex(o => o.PaymentId);
        builder.HasIndex(o => o.CreatedAt);
    }
}

public class RazorpayWebhookEventConfiguration : IEntityTypeConfiguration<RazorpayWebhookEvent>
{
    public void Configure(EntityTypeBuilder<RazorpayWebhookEvent> builder)
    {
        builder.ToTable("RazorpayWebhookEvents");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.EventId).IsRequired().HasMaxLength(100);
        builder.Property(e => e.Event).IsRequired().HasMaxLength(60);
        builder.Property(e => e.OrderId).HasMaxLength(40);
        builder.Property(e => e.PaymentId).HasMaxLength(40);
        builder.Property(e => e.Note).HasMaxLength(500);
        builder.Property(e => e.Payload).IsRequired().HasMaxLength(8000);

        // A redelivery of the same event is recognised by this, even when two arrive at once.
        builder.HasIndex(e => e.EventId).IsUnique();
        builder.HasIndex(e => e.CreatedAt);
    }
}
