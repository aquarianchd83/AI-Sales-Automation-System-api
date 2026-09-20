using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Leads;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class LeadScoringRuleConfiguration : IEntityTypeConfiguration<LeadScoringRule>
{
    public void Configure(EntityTypeBuilder<LeadScoringRule> builder)
    {
        builder.ToTable("LeadScoringRules");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.RuleKey).IsRequired().HasMaxLength(60);
        builder.Property(r => r.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(r => r.RuleType).HasConversion<string>().HasMaxLength(30);
        builder.Property(r => r.MatchValue).IsRequired().HasMaxLength(200);

        builder.HasIndex(r => new { r.TenantId, r.RuleKey }).IsUnique();
        builder.HasIndex(r => new { r.TenantId, r.IsActive, r.SortOrder });
    }
}

public class LeadScoreContributionConfiguration : IEntityTypeConfiguration<LeadScoreContribution>
{
    public void Configure(EntityTypeBuilder<LeadScoreContribution> builder)
    {
        builder.ToTable("LeadScoreContributions");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.SourceKey).IsRequired().HasMaxLength(60);
        builder.Property(c => c.DisplayName).IsRequired().HasMaxLength(100);

        builder.HasIndex(c => new { c.LeadId, c.AppliedAt });

        // Enforces LeadScoringRule.OncePerLead at the database, filtered to the contributions that
        // actually claim to be once-only. A rule with OncePerLead = false writes IsOnce = false rows,
        // which this index ignores - so re-applying it every turn stays legal while a once-only rule
        // physically cannot fire twice, even if two turns race.
        builder.HasIndex(c => new { c.LeadId, c.SourceKey })
            .IsUnique()
            .HasFilter("[IsOnce] = 1");

        builder.HasOne<Lead>()
            .WithMany(l => l.ScoreContributions)
            .HasForeignKey(c => c.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        // SetNull, not Restrict: a deleted rule should not block deleting it, and SourceKey/DisplayName
        // are denormalized precisely so the breakdown survives the rule going away.
        builder.HasOne<LeadScoringRule>()
            .WithMany()
            .HasForeignKey(c => c.RuleId)
            .OnDelete(DeleteBehavior.SetNull);
    }
}
