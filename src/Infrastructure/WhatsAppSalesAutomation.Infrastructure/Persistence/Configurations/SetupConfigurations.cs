using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Billing;
using WhatsAppSalesAutomation.Domain.Entities.Setup;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PlanSetupVersionConfiguration : IEntityTypeConfiguration<PlanSetupVersion>
{
    public void Configure(EntityTypeBuilder<PlanSetupVersion> builder)
    {
        builder.ToTable("PlanSetupVersions");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(v => v.ReleaseNotes).HasMaxLength(1000);

        builder.HasOne<Plan>().WithMany().HasForeignKey(v => v.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(v => new { v.PlanId, v.VersionNumber }).IsUnique();
        builder.HasIndex(v => new { v.PlanId, v.Status });
    }
}

public class PlanRequirementConfiguration : IEntityTypeConfiguration<PlanRequirement>
{
    public void Configure(EntityTypeBuilder<PlanRequirement> builder)
    {
        builder.ToTable("PlanRequirements");
        builder.HasKey(r => r.Id);

        builder.Property(r => r.FieldKey).IsRequired().HasMaxLength(60);
        builder.Property(r => r.Label).IsRequired().HasMaxLength(150);
        builder.Property(r => r.HelpText).HasMaxLength(500);
        builder.Property(r => r.FieldType).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.DefaultValue).HasMaxLength(1000);
        builder.Property(r => r.OptionsJson).HasMaxLength(4000);
        builder.Property(r => r.ValidationJson).HasMaxLength(1000);
        builder.Property(r => r.Section).IsRequired().HasMaxLength(40);
        builder.Property(r => r.ConditionFieldKey).HasMaxLength(60);
        builder.Property(r => r.ConditionOperator).HasConversion<string>().HasMaxLength(20);
        builder.Property(r => r.ConditionValue).HasMaxLength(500);
        builder.Property(r => r.MetricKey).HasMaxLength(40);

        builder.HasOne<PlanSetupVersion>().WithMany().HasForeignKey(r => r.PlanSetupVersionId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(r => new { r.PlanSetupVersionId, r.FieldKey }).IsUnique();
    }
}

public class PlanApplicationConfiguration : IEntityTypeConfiguration<PlanApplication>
{
    public void Configure(EntityTypeBuilder<PlanApplication> builder)
    {
        builder.ToTable("PlanApplications");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.Name).IsRequired().HasMaxLength(150);
        builder.Property(a => a.Status).HasConversion<string>().HasMaxLength(20);
        builder.Property(a => a.SetupStatus).HasConversion<string>().HasMaxLength(20);

        builder.HasOne<Plan>().WithMany().HasForeignKey(a => a.PlanId).OnDelete(DeleteBehavior.Restrict);
        builder.HasOne<PlanSetupVersion>().WithMany().HasForeignKey(a => a.PlanSetupVersionId).OnDelete(DeleteBehavior.Restrict);

        builder.HasIndex(a => new { a.TenantId, a.Name }).IsUnique();
        builder.HasIndex(a => a.PlanSetupVersionId);
    }
}

public class ApplicationSetupValueConfiguration : IEntityTypeConfiguration<ApplicationSetupValue>
{
    public void Configure(EntityTypeBuilder<ApplicationSetupValue> builder)
    {
        builder.ToTable("ApplicationSetupValues");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.FieldKey).IsRequired().HasMaxLength(60);
        builder.Property(v => v.FieldValue).HasMaxLength(4000);

        builder.HasOne<PlanApplication>().WithMany().HasForeignKey(v => v.ApplicationId).OnDelete(DeleteBehavior.Cascade);
        builder.HasIndex(v => new { v.ApplicationId, v.FieldKey }).IsUnique();
    }
}

public class ApplicationSetupAuditEntryConfiguration : IEntityTypeConfiguration<ApplicationSetupAuditEntry>
{
    public void Configure(EntityTypeBuilder<ApplicationSetupAuditEntry> builder)
    {
        builder.ToTable("ApplicationSetupAuditEntries");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.PlanVersionLabel).IsRequired().HasMaxLength(200);
        builder.Property(e => e.Action).HasConversion<string>().HasMaxLength(30);
        builder.Property(e => e.FieldKey).HasMaxLength(60);
        builder.Property(e => e.PreviousValue).HasMaxLength(4000);
        builder.Property(e => e.NewValue).HasMaxLength(4000);
        builder.Property(e => e.Reason).HasMaxLength(500);

        // No cascade: the trail must outlive anything that could be deleted around it.
        builder.HasOne<PlanApplication>().WithMany().HasForeignKey(e => e.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.ApplicationId, e.PerformedAt });
    }
}

public class ApplicationExecutionConfiguration : IEntityTypeConfiguration<ApplicationExecution>
{
    public void Configure(EntityTypeBuilder<ApplicationExecution> builder)
    {
        builder.ToTable("ApplicationExecutions");
        builder.HasKey(e => e.Id);

        builder.Property(e => e.PlanVersionLabel).IsRequired().HasMaxLength(200);
        builder.Property(e => e.SetupSnapshotJson).IsRequired();

        builder.HasOne<PlanApplication>().WithMany().HasForeignKey(e => e.ApplicationId).OnDelete(DeleteBehavior.Restrict);
        builder.HasIndex(e => new { e.ApplicationId, e.StartedAt });
    }
}
