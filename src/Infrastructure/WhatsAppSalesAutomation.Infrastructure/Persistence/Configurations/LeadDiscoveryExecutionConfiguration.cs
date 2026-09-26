using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.LeadDiscovery;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class LeadDiscoveryExecutionConfiguration : IEntityTypeConfiguration<LeadDiscoveryExecution>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryExecution> builder)
    {
        builder.ToTable("LeadDiscoveryExecutions");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.ProfileName).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Trigger).IsRequired().HasMaxLength(30);
        builder.Property(e => e.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.FailedStep).HasMaxLength(50);
        builder.Property(e => e.ErrorMessage).HasMaxLength(2000);
        builder.Property(e => e.ErrorDetails).HasMaxLength(8000);
        builder.Property(e => e.NextRetryInfo).HasMaxLength(500);
        builder.Property(e => e.LockKey).IsRequired().HasMaxLength(100);
        builder.Property(e => e.LockStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.LockTokenReference).HasMaxLength(20);
        builder.Property(e => e.LockOwnerInstanceId).HasMaxLength(200);
        builder.Property(e => e.ReferredCampaignName).HasMaxLength(200);
        builder.Property(e => e.GeneratedCampaignName).HasMaxLength(200);
        builder.Property(e => e.CampaignStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.CampaignNote).HasMaxLength(1000);
        builder.Property(e => e.TemplateStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.MappingStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.Summary).HasMaxLength(2000);

        // History is read by processing date; retry lookups by profile and status.
        builder.HasIndex(e => new { e.TenantId, e.ProcessingDate });
        builder.HasIndex(e => new { e.TenantId, e.LeadDiscoveryProfileId, e.Status });
        builder.HasIndex(e => e.RootExecutionId);
    }
}

public class LeadDiscoveryExecutionCustomerConfiguration : IEntityTypeConfiguration<LeadDiscoveryExecutionCustomer>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryExecutionCustomer> builder)
    {
        builder.ToTable("LeadDiscoveryExecutionCustomers");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.CustomerName).IsRequired().HasMaxLength(300);
        builder.Property(c => c.Phone).HasMaxLength(50);
        builder.Property(c => c.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(c => c.ErrorMessage).HasMaxLength(2000);
        builder.Property(c => c.ErrorDetails).HasMaxLength(8000);
        builder.Property(c => c.CandidateJson).HasColumnType("nvarchar(max)");

        builder.HasOne<LeadDiscoveryExecution>()
            .WithMany()
            .HasForeignKey(c => c.ExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(c => new { c.ExecutionId, c.Status });
    }
}

public class LeadDiscoveryExecutionTemplateConfiguration : IEntityTypeConfiguration<LeadDiscoveryExecutionTemplate>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryExecutionTemplate> builder)
    {
        builder.ToTable("LeadDiscoveryExecutionTemplates");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.TemplateName).IsRequired().HasMaxLength(200);
        builder.Property(t => t.Status).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.ErrorMessage).HasMaxLength(2000);

        builder.HasOne<LeadDiscoveryExecution>()
            .WithMany()
            .HasForeignKey(t => t.ExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.ExecutionId, t.Sequence }).IsUnique();
    }
}

public class LeadDiscoveryLockTransitionConfiguration : IEntityTypeConfiguration<LeadDiscoveryLockTransition>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryLockTransition> builder)
    {
        builder.ToTable("LeadDiscoveryLockTransitions");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.FromStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.ToStatus).HasConversion<string>().HasMaxLength(30);
        builder.Property(t => t.LockTokenReference).HasMaxLength(20);
        builder.Property(t => t.OwnerInstanceId).HasMaxLength(200);
        builder.Property(t => t.Reason).HasMaxLength(500);
        builder.Property(t => t.Error).HasMaxLength(2000);

        builder.HasOne<LeadDiscoveryExecution>()
            .WithMany()
            .HasForeignKey(t => t.ExecutionId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.HasIndex(t => new { t.ExecutionId, t.TransitionAtUtc });
    }
}

public class LeadDiscoveryGeneratedCampaignConfiguration : IEntityTypeConfiguration<LeadDiscoveryGeneratedCampaign>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryGeneratedCampaign> builder)
    {
        builder.ToTable("LeadDiscoveryGeneratedCampaigns");
        builder.HasKey(g => g.Id);

        // Campaign idempotency: at most one generated campaign per logical key, whatever the application
        // layer does. The losing side of a race gets a unique violation and reuses the winner's campaign.
        builder.HasIndex(g => new { g.TenantId, g.LeadDiscoveryProfileId, g.AutoCampaignId, g.ProcessingDate }).IsUnique();
        builder.HasIndex(g => g.CampaignId).IsUnique();
    }
}

public class LeadDiscoveryLockConfiguration : IEntityTypeConfiguration<LeadDiscoveryLock>
{
    public void Configure(EntityTypeBuilder<LeadDiscoveryLock> builder)
    {
        builder.ToTable("LeadDiscoveryLocks");

        // The key itself is the lock: the primary key makes a second concurrent insert for the same key fail.
        builder.HasKey(l => l.LockKey);
        builder.Property(l => l.LockKey).HasMaxLength(100);
        builder.Property(l => l.OwnerInstanceId).IsRequired().HasMaxLength(200);
        builder.Property(l => l.Status).HasConversion<string>().HasMaxLength(30);
    }
}
