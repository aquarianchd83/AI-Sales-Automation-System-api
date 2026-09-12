using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Platform;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class TenantJobScheduleConfiguration : IEntityTypeConfiguration<TenantJobSchedule>
{
    public void Configure(EntityTypeBuilder<TenantJobSchedule> builder)
    {
        builder.ToTable("TenantJobSchedules");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.JobType).IsRequired().HasMaxLength(100);
        builder.Property(s => s.CronExpression).IsRequired().HasMaxLength(100);
        builder.Property(s => s.LastRunSummary).HasMaxLength(2000);

        // One row per tenant per job type, enforced in the database and not only by the provisioner's
        // own "create the missing ones" pass: that pass runs concurrently with itself (startup reconcile,
        // the reconcile job, and any console read can all hit a fresh tenant at once), and two of
        // them racing would otherwise leave duplicate rows whose schedules silently disagree.
        builder.HasIndex(s => new { s.TenantId, s.JobType }).IsUnique();

        // TenantId is a plain foreign key here, not ITenantOwned tenancy - see the entity's own doc
        // comment. Cascade so a physically deleted tenant cannot leave schedule rows behind; the normal
        // operator "delete" is a status change, which leaves the rows (and unregisters the jobs) on
        // purpose so reactivating restores the operator's own schedules rather than the defaults.
        builder.HasOne<Domain.Entities.Tenancy.Tenant>()
            .WithMany()
            .HasForeignKey(s => s.TenantId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
