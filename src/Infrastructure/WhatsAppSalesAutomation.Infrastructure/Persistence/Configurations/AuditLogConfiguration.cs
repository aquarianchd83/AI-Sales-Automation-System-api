using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Audit;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AuditLogConfiguration : IEntityTypeConfiguration<AuditLog>
{
    public void Configure(EntityTypeBuilder<AuditLog> builder)
    {
        builder.ToTable("AuditLogs");
        builder.HasKey(a => a.Id);

        builder.Property(a => a.EntityName).IsRequired().HasMaxLength(60);
        builder.Property(a => a.Action).HasConversion<string>().IsRequired().HasMaxLength(20);
        builder.Property(a => a.ChangesJson).IsRequired().HasColumnType("nvarchar(max)");
        builder.Property(a => a.IpAddress).HasMaxLength(45);   // longest IPv6 text form

        // The list screen: newest first for one tenant.
        builder.HasIndex(a => new { a.TenantId, a.PerformedAt }).HasDatabaseName("IX_AuditLogs_Tenant_PerformedAt");

        // "The history of this lead": every entry for one entity.
        builder.HasIndex(a => new { a.TenantId, a.EntityName, a.EntityId }).HasDatabaseName("IX_AuditLogs_Tenant_Entity");

        // "What did this person do": filtered by actor.
        builder.HasIndex(a => new { a.TenantId, a.PerformedBy }).HasDatabaseName("IX_AuditLogs_Tenant_Actor");
    }
}
