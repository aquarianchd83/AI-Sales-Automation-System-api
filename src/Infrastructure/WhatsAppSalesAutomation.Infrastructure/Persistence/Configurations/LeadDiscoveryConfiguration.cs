using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Application.LeadDiscovery;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class LeadDiscoveryProfileConfiguration : IEntityTypeConfiguration<LeadDiscoveryProfile>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryProfile> builder)
    {
        builder.ToTable("LeadDiscoveryProfiles");
        builder.HasKey(p => p.Id);

        // One profile per tenant.
        builder.HasIndex(p => p.TenantId).IsUnique();

        builder.Property(p => p.TargetBusinessType).IsRequired().HasMaxLength(LeadDiscoveryLimits.TargetBusinessType);

        // Short lists only ever read and written whole - JSON columns, as Tenant.DomainKeywords.
        builder.Property(p => p.Keywords).AsJsonStringList();
        builder.Property(p => p.Locations).AsJsonStringList();
        builder.Property(p => p.RequiredFields).AsJsonStringList();
        builder.Property(p => p.AdditionalCriteria).AsJsonStringList();
    }
}

public class DiscoveredLeadConfiguration : IEntityTypeConfiguration<DiscoveredLead>
{
    public void Configure(EntityTypeBuilder<DiscoveredLead> builder)
    {
        builder.ToTable("DiscoveredLeads");
        builder.HasKey(l => l.Id);

        builder.Property(l => l.BusinessName).IsRequired().HasMaxLength(LeadDiscoveryLimits.BusinessName);
        builder.Property(l => l.BusinessType).IsRequired().HasMaxLength(LeadDiscoveryLimits.BusinessType);
        builder.Property(l => l.ContactPerson).HasMaxLength(LeadDiscoveryLimits.ContactPerson);
        builder.Property(l => l.Address).HasMaxLength(LeadDiscoveryLimits.Address);
        builder.Property(l => l.City).HasMaxLength(LeadDiscoveryLimits.City);
        builder.Property(l => l.State).HasMaxLength(LeadDiscoveryLimits.State);
        builder.Property(l => l.Phone).HasMaxLength(LeadDiscoveryLimits.Phone);
        builder.Property(l => l.PhoneE164).HasMaxLength(LeadDiscoveryLimits.PhoneE164);
        builder.Property(l => l.PhoneSourceUrl).HasMaxLength(LeadDiscoveryLimits.Url);
        builder.Property(l => l.Email).HasMaxLength(LeadDiscoveryLimits.Email);
        builder.Property(l => l.Website).HasMaxLength(LeadDiscoveryLimits.Url);
        builder.Property(l => l.SourceUrl).IsRequired().HasMaxLength(LeadDiscoveryLimits.Url);
        builder.Property(l => l.ScoreRationale).HasMaxLength(LeadDiscoveryLimits.ScoreRationale);
        builder.Property(l => l.PhoneKey).HasMaxLength(LeadDiscoveryLimits.PhoneKey);
        builder.Property(l => l.WebsiteKey).HasMaxLength(LeadDiscoveryLimits.DedupeKey);
        builder.Property(l => l.NameKey).IsRequired().HasMaxLength(LeadDiscoveryLimits.DedupeKey);

        // Not unique: the dedupe keys are deliberately loose matches (a shared number, a name in one city),
        // and duplicates are excluded by the run itself, which never overlaps another run for its tenant.
        builder.HasIndex(l => new { l.TenantId, l.PhoneKey });
        builder.HasIndex(l => new { l.TenantId, l.WebsiteKey });
        builder.HasIndex(l => new { l.TenantId, l.NameKey });
        builder.HasIndex(l => new { l.TenantId, l.CreatedAt });
    }
}

public class LeadDiscoveryRunConfiguration : IEntityTypeConfiguration<LeadDiscoveryRun>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryRun> builder)
    {
        builder.ToTable("LeadDiscoveryRuns");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.Model).IsRequired().HasMaxLength(100);

        // Exact decimal rather than a float, and six places because a cheap run costs fractions of a cent -
        // two would round a real charge down to zero.
        builder.Property(r => r.EstimatedCostUsd).HasPrecision(18, 6);

        // Every read is one tenant's runs, newest first or within a month.
        builder.HasIndex(r => new { r.TenantId, r.RanAtUtc });
    }
}

internal static class JsonStringListPropertyBuilderExtensions
{
    /// <summary>Stores a string list as a JSON array in one nvarchar(max) column. The comparer makes EF see
    /// in-place list edits; an empty column reads back as an empty list.</summary>
    public static PropertyBuilder<List<string>> AsJsonStringList(this PropertyBuilder<List<string>> property) =>
        property
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
}
