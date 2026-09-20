using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Leads;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

/// <summary>Config for the tenant's qualification schema. Grouped in one file like
/// QuotaConfigurations, since the two types are only ever changed together.</summary>
public class QualificationFieldConfiguration : IEntityTypeConfiguration<QualificationField>
{
    public void Configure(EntityTypeBuilder<QualificationField> builder)
    {
        builder.ToTable("QualificationFields");
        builder.HasKey(f => f.Id);

        builder.Property(f => f.FieldKey).IsRequired().HasMaxLength(60);
        builder.Property(f => f.DisplayName).IsRequired().HasMaxLength(100);
        builder.Property(f => f.Description).HasMaxLength(500);
        builder.Property(f => f.Question).IsRequired().HasMaxLength(300);
        builder.Property(f => f.DataType).HasConversion<string>().HasMaxLength(20);
        builder.Property(f => f.AllowedValuesJson).HasColumnType("nvarchar(max)");
        builder.Property(f => f.ValidationPattern).HasMaxLength(500);

        // The filtered unique index is what makes "one field per key per tenant" a database
        // invariant rather than a service-layer convention - the key is what the model returns, and
        // two rows answering to the same key would make a captured value ambiguous.
        builder.HasIndex(f => new { f.TenantId, f.FieldKey })
            .IsUnique()
            .HasFilter("[IsDeleted] = 0");

        // Covers the planner's hot path: active fields for this tenant, already in ask order.
        builder.HasIndex(f => new { f.TenantId, f.IsActive, f.Priority, f.SortOrder })
            .HasFilter("[IsDeleted] = 0");

        // No HasQueryFilter here - QualificationField is both ISoftDelete and ITenantOwned, and the
        // combined filter is built reflectively in ApplicationDbContext.OnModelCreating.
    }
}

public class LeadQualificationValueConfiguration : IEntityTypeConfiguration<LeadQualificationValue>
{
    public void Configure(EntityTypeBuilder<LeadQualificationValue> builder)
    {
        builder.ToTable("LeadQualificationValues");
        builder.HasKey(v => v.Id);

        builder.Property(v => v.FieldKey).IsRequired().HasMaxLength(60);
        builder.Property(v => v.RawValue).IsRequired().HasMaxLength(1000);
        builder.Property(v => v.NormalizedValue).HasMaxLength(1000);

        // At most one current value per (lead, field). Supersede-then-insert is the only way to
        // change a value, so this index is what stops a partially-applied capture leaving two.
        builder.HasIndex(v => new { v.LeadId, v.FieldKey })
            .IsUnique()
            .HasFilter("[IsSuperseded] = 0");

        builder.HasIndex(v => new { v.LeadId, v.IsSuperseded });

        // Cascade: captured answers are owned by the lead, the same way LeadActivity is.
        builder.HasOne<Lead>()
            .WithMany(l => l.QualificationValues)
            .HasForeignKey(v => v.LeadId)
            .OnDelete(DeleteBehavior.Cascade);

        // Restrict: deleting a field the AI has already captured answers for should fail loudly
        // rather than silently erase what customers told us. Fields are deactivated, not deleted -
        // see QualificationField.IsActive.
        builder.HasOne<QualificationField>()
            .WithMany()
            .HasForeignKey(v => v.FieldId)
            .OnDelete(DeleteBehavior.Restrict);
    }
}
