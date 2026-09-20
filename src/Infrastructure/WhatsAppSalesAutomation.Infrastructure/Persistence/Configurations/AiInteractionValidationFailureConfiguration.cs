using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Ai;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AiInteractionValidationFailureConfiguration : IEntityTypeConfiguration<AiInteractionValidationFailure>
{
    public void Configure(EntityTypeBuilder<AiInteractionValidationFailure> builder)
    {
        builder.ToTable("AiInteractionValidationFailures");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.Code).IsRequired().HasMaxLength(60);
        builder.Property(f => f.Detail).HasMaxLength(500);

        // The report groups by code inside a date window, and CreatedAt is the window column.
        builder.HasIndex(f => new { f.Code, f.CreatedAt });
        builder.HasIndex(f => f.AiInteractionId);

        // Cascade, matching AiInteractionSource: these rows describe one turn and mean nothing
        // without it.
        builder.HasOne<AiInteraction>()
            .WithMany()
            .HasForeignKey(f => f.AiInteractionId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
