using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Tenancy;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class TenantConfiguration : IEntityTypeConfiguration<Tenant>
{
    public void Configure(EntityTypeBuilder<Tenant> builder)
    {
        builder.ToTable("Tenants");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Slug).IsRequired().HasMaxLength(63);
        builder.HasIndex(t => t.Slug).IsUnique();

        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(t => t.CountryCode).HasMaxLength(2);
        builder.Property(t => t.Timezone).HasMaxLength(50);

        builder.Property(t => t.ProductName).HasMaxLength(200);
        builder.Property(t => t.Industry).HasMaxLength(100);
        builder.Property(t => t.BusinessDescription).HasMaxLength(2000);
        builder.Property(t => t.WebsiteUrl).HasMaxLength(300);
        builder.Property(t => t.SupportEmail).HasMaxLength(256);
        builder.Property(t => t.SupportPhone).HasMaxLength(32);
        builder.Property(t => t.BillingAlertEmail).HasMaxLength(256);
        builder.Property(t => t.BillingAlertPhoneE164).HasMaxLength(32);

        // A short list only ever read and written whole, so a JSON column rather than a child table. The
        // comparer makes EF detect in-place list edits; an empty column (existing rows) reads as no keywords.
        builder.Property(t => t.DomainKeywords)
            .HasColumnType("nvarchar(max)")
            .IsRequired()
            .HasConversion(
                v => JsonSerializer.Serialize(v, (JsonSerializerOptions?)null),
                v => string.IsNullOrEmpty(v)
                    ? new List<string>()
                    : JsonSerializer.Deserialize<List<string>>(v, (JsonSerializerOptions?)null) ?? new List<string>(),
                new ValueComparer<List<string>>(
                    (a, b) => a!.SequenceEqual(b!),
                    v => v.Aggregate(0, (hash, s) => HashCode.Combine(hash, s.GetHashCode())),
                    v => v.ToList()));

        // No FK constraint to ApplicationUser/Plan by design - same "loose Guid" treatment every other
        // cross-aggregate reference in this codebase already uses (AssignedAgentId, CreatedBy, etc.).
    }
}
