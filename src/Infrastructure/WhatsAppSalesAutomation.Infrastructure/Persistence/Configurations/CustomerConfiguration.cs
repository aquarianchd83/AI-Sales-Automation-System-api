using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Domain.Entities.Customers;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class CustomerConfiguration : IEntityTypeConfiguration<Customer>
{
    public void Configure(EntityTypeBuilder<Customer> builder)
    {
        builder.ToTable("Customers");
        builder.HasKey(c => c.Id);

        builder.Property(c => c.PhoneNumberE164).IsRequired().HasMaxLength(20);
        builder.HasIndex(c => c.PhoneNumberE164).IsUnique();

        builder.Property(c => c.FirstName).HasMaxLength(100);
        builder.Property(c => c.LastName).HasMaxLength(100);
        builder.Property(c => c.Email).HasMaxLength(256);
        builder.Property(c => c.Source).HasMaxLength(100);
        builder.Property(c => c.PreferredLanguage).HasMaxLength(10);
        builder.Property(c => c.OptInStatus).HasConversion<string>().HasMaxLength(20);

        builder.HasMany(c => c.Tags)
            .WithMany(t => t.Customers)
            .UsingEntity(j => j.ToTable("CustomerTagMap"));

        builder.HasQueryFilter(c => !c.IsDeleted);
    }
}
