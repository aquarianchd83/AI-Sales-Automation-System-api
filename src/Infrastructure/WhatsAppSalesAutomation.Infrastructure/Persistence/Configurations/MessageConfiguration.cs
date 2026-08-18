using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class MessageConfiguration : IEntityTypeConfiguration<Message>
{
    public void Configure(EntityTypeBuilder<Message> builder)
    {
        builder.ToTable("Messages");
        builder.HasKey(m => m.Id);

        builder.Property(m => m.Direction).HasConversion<string>().HasMaxLength(10);
        builder.Property(m => m.MessageType).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Text).HasMaxLength(4000);
        builder.Property(m => m.TemplateName).HasMaxLength(200);
        builder.Property(m => m.FailureReason).HasMaxLength(1000);

        builder.Property(m => m.IdempotencyKey).IsRequired().HasMaxLength(100);
        builder.HasIndex(m => m.IdempotencyKey).IsUnique();

        // Filtered rather than plain unique: WhatsAppMessageId is null until the send succeeds, and
        // SQL Server's default unique index treats multiple NULLs as a violation without a WHERE filter.
        builder.Property(m => m.WhatsAppMessageId).HasMaxLength(100);
        builder.HasIndex(m => m.WhatsAppMessageId).IsUnique().HasFilter("[WhatsAppMessageId] IS NOT NULL");

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(m => m.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<CampaignCustomer>()
            .WithMany()
            .HasForeignKey(m => m.CampaignCustomerId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Conversation>()
            .WithMany()
            .HasForeignKey(m => m.ConversationId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(m => m.ConversationId);
    }
}
