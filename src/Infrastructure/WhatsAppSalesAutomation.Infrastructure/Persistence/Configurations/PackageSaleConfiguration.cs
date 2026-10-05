using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Customers;
using WhatsAppSalesAutomation.Domain.Entities.Packages;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class PackageSaleConfiguration : IEntityTypeConfiguration<PackageSale>
{
    public void Configure(EntityTypeBuilder<PackageSale> builder)
    {
        builder.ToTable("PackageSales");
        builder.HasKey(s => s.Id);

        builder.Property(s => s.Amount).HasColumnType("decimal(18,2)");

        // Restrict: a package with sales is deactivated, never deleted, so the report can always name it.
        builder.HasOne<SalesPackage>()
            .WithMany()
            .HasForeignKey(s => s.PackageId)
            .OnDelete(DeleteBehavior.Restrict);

        builder.HasOne<Customer>()
            .WithMany()
            .HasForeignKey(s => s.CustomerId)
            .OnDelete(DeleteBehavior.SetNull);

        // The report always reads one tenant's sales over a date range.
        builder.HasIndex(s => new { s.TenantId, s.SoldAt });
        builder.HasIndex(s => s.PackageId);
        builder.HasIndex(s => s.CustomerId);
    }
}
