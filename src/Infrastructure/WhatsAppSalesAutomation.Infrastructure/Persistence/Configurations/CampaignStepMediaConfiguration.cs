using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Campaigns;
using WhatsAppSalesAutomation.Domain.Entities.Media;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class CampaignStepMediaConfiguration : IEntityTypeConfiguration<CampaignStepMedia>
{
    public void Configure(EntityTypeBuilder<CampaignStepMedia> builder)
    {
        builder.ToTable("CampaignStepMedia");
        builder.HasKey(m => m.Id);

        builder.HasIndex(m => new { m.CampaignStepId, m.MediaAssetId }).IsUnique();

        // Restrict rather than cascade: deleting a MediaAsset that a step still uses should fail
        // loudly (MediaService.DeleteAsync already blocks this at the Application layer) rather than
        // silently dropping a step below its required media count.
        builder.HasOne<MediaAsset>()
            .WithMany()
            .HasForeignKey(m => m.MediaAssetId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
