using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlatformConversationConfiguration : IEntityTypeConfiguration<PlatformConversation>
{
    public void Configure(EntityTypeBuilder<PlatformConversation> builder)
    {
        builder.ToTable("PlatformConversations");
        builder.HasKey(c => c.Id);
        builder.Property(c => c.PhoneE164).IsRequired().HasMaxLength(20);
        builder.Property(c => c.ContactName).HasMaxLength(200);
        builder.Property(c => c.LastMessagePreview).HasMaxLength(200);

        builder.HasIndex(c => c.PhoneE164).IsUnique();
        builder.HasIndex(c => c.TenantId);
        builder.HasIndex(c => c.LastMessageAtUtc);

        builder.HasMany(c => c.Messages).WithOne(m => m.Conversation).HasForeignKey(m => m.ConversationId).OnDelete(DeleteBehavior.Cascade);
    }
}

public class PlatformConversationMessageConfiguration : IEntityTypeConfiguration<PlatformConversationMessage>
{
    public void Configure(EntityTypeBuilder<PlatformConversationMessage> builder)
    {
        builder.ToTable("PlatformConversationMessages");
        builder.HasKey(m => m.Id);
        builder.Property(m => m.Direction).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(m => m.Body).HasMaxLength(4096);
        builder.Property(m => m.MessageType).IsRequired().HasMaxLength(30);
        builder.Property(m => m.TemplateName).HasMaxLength(512);
        builder.Property(m => m.WhatsAppMessageId).HasMaxLength(200);
        builder.Property(m => m.ErrorMessage).HasMaxLength(1000);
        builder.Property(m => m.SentByEmail).HasMaxLength(256);

        builder.HasIndex(m => new { m.ConversationId, m.SentAtUtc });
        builder.HasIndex(m => m.WhatsAppMessageId);
    }
}
