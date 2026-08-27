using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Infrastructure.WhatsApp;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class WhatsAppAccessTokenStateConfiguration : IEntityTypeConfiguration<WhatsAppAccessTokenState>
{
    public void Configure(EntityTypeBuilder<WhatsAppAccessTokenState> builder)
    {
        builder.ToTable("WhatsAppAccessTokenStates");
        builder.HasKey(s => s.Id);

        // Client-set, not identity-generated - Id is always the fixed singleton value 1 (see the
        // entity's own doc comment), never auto-incremented.
        builder.Property(s => s.Id).ValueGeneratedNever();

        builder.Property(s => s.AccessToken).IsRequired().HasColumnType("nvarchar(max)");
    }
}
