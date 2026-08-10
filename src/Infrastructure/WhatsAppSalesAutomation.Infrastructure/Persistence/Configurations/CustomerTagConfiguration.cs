using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class CustomerTagConfiguration : IEntityTypeConfiguration<CustomerTag>
{
    public void Configure(EntityTypeBuilder<CustomerTag> builder)
    {
        builder.ToTable("CustomerTags");
        builder.HasKey(t => t.Id);

        builder.Property(t => t.Name).IsRequired().HasMaxLength(100);
        builder.HasIndex(t => t.Name).IsUnique();
    }
}
