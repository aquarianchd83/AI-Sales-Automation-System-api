using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlatformMessageTemplateConfiguration : IEntityTypeConfiguration<PlatformMessageTemplate>
{
    public void Configure(EntityTypeBuilder<PlatformMessageTemplate> builder)
    {
        builder.ToTable("PlatformMessageTemplates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.EventKey).IsRequired().HasMaxLength(30);
        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Language).IsRequired().HasMaxLength(10);
        builder.Property(t => t.WhatsAppTemplateName).IsRequired().HasMaxLength(200);
        builder.Property(t => t.BodyText).IsRequired().HasMaxLength(2000);
        builder.Property(t => t.Category).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.WhatsAppTemplateStatus).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.MetaTemplateId).HasMaxLength(100);
        builder.Property(t => t.LastPushedBodyText).HasMaxLength(2000);

        // Restrict: deleting an image a template still shows is refused by PlatformMediaService before it gets here.
        builder.HasOne<PlatformMediaAsset>()
            .WithMany()
            .HasForeignKey(t => t.HeaderMediaAssetId)
            .OnDelete(DeleteBehavior.Restrict);

        // One template per kind of notice, and Meta scopes names by (name, language).
        builder.HasIndex(t => t.EventKey).IsUnique();
        builder.HasIndex(t => new { t.WhatsAppTemplateName, t.Language }).IsUnique();
    }
}
