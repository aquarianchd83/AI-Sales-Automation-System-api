using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Conversations;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class ConversationConfiguration : IEntityTypeConfiguration<Conversation>
{
    /// <summary>Shadow, computed: see Configure. Read with <c>EF.Property&lt;DateTime&gt;(c, "ActivityAt")</c>.</summary>
    public const string ConversationActivityAt = "ActivityAt";

    public void Configure(EntityTypeBuilder<Conversation> builder)
    {
        builder.ToTable("Conversations");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Mode).HasConversion<string>().HasMaxLength(10);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(10);
        builder.Property(c => c.LastDetectedIntent).HasMaxLength(100);
        builder.Property(c => c.LastLeadScore).HasConversion<string>().HasMaxLength(10);
        builder.Property(c => c.Summary).HasColumnType("nvarchar(max)");

        // Not unique: history is kept (a customer can have several Closed conversations over time),
        // just indexed for the "does this customer already have an active thread" lookup.
        builder.HasIndex(c => new { c.CustomerId, c.Status });

        // The inbox sort key: last message time, or creation time for a conversation with no message yet. A persisted computed
        // column (shadow property, never written by the app) so the inbox can be ordered straight from an index - ordering by
        // COALESCE(LastMessageAt, CreatedAt) in the query itself made SQL Server sort the tenant's whole conversation set on
        // every page. LastMessageAt keeps its meaning (null = no message yet) for the API.
        builder.Property<DateTime>(ConversationActivityAt)
            .HasComputedColumnSql("COALESCE([LastMessageAt], [CreatedAt])", stored: true);

        // The inbox list: every read is scoped by the tenant filter, optionally narrowed by status, newest activity first.
        builder.HasIndex(nameof(Conversation.TenantId), ConversationActivityAt);
        builder.HasIndex(nameof(Conversation.TenantId), nameof(Conversation.Status), ConversationActivityAt);

        // Restrict, matching Customer's other references (CampaignCustomer, Message): customers are
        // soft-deleted, never hard-deleted, so this should never actually need to cascade or null out.
        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(c => c.CustomerId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
