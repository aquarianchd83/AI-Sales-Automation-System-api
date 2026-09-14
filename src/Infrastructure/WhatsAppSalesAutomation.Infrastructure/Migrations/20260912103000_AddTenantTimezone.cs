using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantTimezone : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Timezone",
                table: "Tenants",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            // Seed every pre-existing tenant with the platform's original, always-assumed timezone -
            // the same IST default ITenantTimeZoneProvider falls back to for a tenant with no value
            // set. This makes that default explicit in the data (visible in the Tenants table, in the
            // signup/self-service/PlatformSuperAdmin timezone pickers, and in TenantProfileDto/
            // PlatformTenantDetailDto) instead of only ever appearing implicitly at read time. No
            // scheduling behavior changes for any existing tenant - Asia/Kolkata is exactly what
            // GetLocalNowAsync already resolved to for them before this column existed.
            migrationBuilder.Sql(
                "UPDATE [Tenants] SET [Timezone] = N'Asia/Kolkata' WHERE [Timezone] IS NULL;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Timezone",
                table: "Tenants");
        }
    }
}
