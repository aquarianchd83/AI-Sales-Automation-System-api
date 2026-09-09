using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using WhatsAppSalesAutomation.Infrastructure.Settings;

namespace WhatsAppSalesAutomation.Infrastructure.Persistence.Configurations;

public class AppSettingConfiguration : IEntityTypeConfiguration<AppSetting>
{
    public void Configure(EntityTypeBuilder<AppSetting> builder)
    {
        builder.ToTable("AppSettings");
        builder.HasKey(s => s.Key);

        builder.Property(s => s.Key).HasMaxLength(200).ValueGeneratedNever();
        builder.Property(s => s.Value).HasColumnType("nvarchar(max)");
    }
}
