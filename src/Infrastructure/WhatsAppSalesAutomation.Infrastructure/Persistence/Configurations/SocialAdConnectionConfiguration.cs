using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.SocialAds;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class SocialAdConnectionConfiguration : IEntityTypeConfiguration<SocialAdConnection>
{
    public void Configure(EntityTypeBuilder<SocialAdConnection> builder)
    {
        builder.ToTable("SocialAdConnections");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.AdAccountId).HasMaxLength(40);
        builder.Property(c => c.AdAccountName).HasMaxLength(200);
        builder.Property(c => c.CurrencyCode).HasMaxLength(3);
        builder.Property(c => c.AccessToken).IsRequired().HasMaxLength(2000);
        builder.Property(c => c.CandidateAccountsJson).HasMaxLength(4000);
        builder.Property(c => c.LastSyncError).HasMaxLength(500);

        // One connection per tenant.
        builder.HasIndex(c => c.TenantId).IsUnique();
    }
}

public class SocialAdSpendConfiguration : IEntityTypeConfiguration<SocialAdSpend>
{
    public void Configure(EntityTypeBuilder<SocialAdSpend> builder)
    {
        builder.ToTable("SocialAdSpends");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Source).IsRequired().HasMaxLength(10);
        builder.Property(s => s.AdAccountId).IsRequired().HasMaxLength(40);
        builder.Property(s => s.Date).HasColumnType("date");
        builder.Property(s => s.Platform).IsRequired().HasMaxLength(30);
        builder.Property(s => s.Spend).HasColumnType("decimal(18,2)");
        builder.Property(s => s.CurrencyCode).IsRequired().HasMaxLength(3);

        // A re-sync of the same day replaces the row, never adds a second one.
        builder.HasIndex(s => new { s.TenantId, s.Source, s.AdAccountId, s.Date, s.Platform }).IsUnique();
        builder.HasIndex(s => new { s.TenantId, s.Date });
    }
}
