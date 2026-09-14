using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Identity;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class RefreshTokenConfiguration : IEntityTypeConfiguration<RefreshToken>
{
    public void Configure(EntityTypeBuilder<RefreshToken> builder)
    {
        builder.ToTable("RefreshTokens");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.TokenHash).IsRequired().HasMaxLength(512);
        builder.HasIndex(r => r.TokenHash).IsUnique();

        // IsRequired(false): ApplicationUser now carries a tenant query filter and RefreshToken
        // deliberately does not (see RefreshToken.TenantId's own doc comment) - EF Core warns if a
        // required relationship's principal side is filtered while the dependent isn't, since a
        // filtered-out User would otherwise leave an "orphaned" RefreshToken row looking illegally
        // parentless to a query that Includes it. The FK column itself is still NOT NULL
        // (UserId is a plain Guid) - this only changes how EF reasons about the navigation/filter
        // interaction, not the database constraint.
        builder.HasOne(r => r.User)
            .WithMany(u => u.RefreshTokens)
            .HasForeignKey(r => r.UserId)
            .IsRequired(false)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
