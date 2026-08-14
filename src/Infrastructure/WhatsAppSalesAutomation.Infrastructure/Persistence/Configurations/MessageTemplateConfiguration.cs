using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Messaging;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class MessageTemplateConfiguration : IEntityTypeConfiguration<MessageTemplate>
{
    public void Configure(EntityTypeBuilder<MessageTemplate> builder)
    {
        builder.ToTable("MessageTemplates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Language).IsRequired().HasMaxLength(10);
        builder.Property(t => t.WhatsAppTemplateName).IsRequired().HasMaxLength(200);
        builder.Property(t => t.BodyText).IsRequired().HasMaxLength(2000);
        builder.Property(t => t.Category).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.WhatsAppTemplateStatus).HasConversion<string>().HasMaxLength(20);

        // Meta scopes template names by (name, language) - the same template name commonly exists
        // once per language, e.g. "welcome_offer" in both en and hi.
        builder.HasIndex(t => new { t.WhatsAppTemplateName, t.Language }).IsUnique();
    }
}
