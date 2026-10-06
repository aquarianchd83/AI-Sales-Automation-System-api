using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using WhatsAppSalesAutomation.Infrastructure.Persistence;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <summary>
    /// The guided application setup now keeps business and audience answers on the tenant's profile instead of in
    /// ApplicationSetupValues (see Application.Setup.SetupProfileBindings). This adds the three audience columns the
    /// profile lacked, then copies each tenant's most recent existing answer onto the profile wherever the profile
    /// is still blank, so nothing a Talent already typed into the wizard is lost. The old rows are left in place and
    /// are simply no longer read.
    /// </summary>
    [DbContext(typeof(ApplicationDbContext))]
    [Migration("20261005170000_AddTenantAudienceProfile")]
    public partial class AddTenantAudienceProfile : Migration
    {
        /// <summary>(setup field key, Tenants column, column width). brand_name is not copied: the tenant always has a name.</summary>
        private static readonly (string Key, string Column, int Width)[] Copies =
        {
            ("business_description", "BusinessDescription", 2000),
            ("website", "WebsiteUrl", 300),
            ("industry", "Industry", 100),
            ("contact_email", "SupportEmail", 256),
            ("contact_phone", "SupportPhone", 32),
            ("business_hours", "WorkingHours", 500),
            ("target_audience", "TargetAudience", 1000),
            ("target_location", "TargetLocation", 500),
            ("target_customer_type", "TargetCustomerType", 50),
        };

        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "TargetAudience",
                table: "Tenants",
                type: "nvarchar(1000)",
                maxLength: 1000,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetCustomerType",
                table: "Tenants",
                type: "nvarchar(50)",
                maxLength: 50,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "TargetLocation",
                table: "Tenants",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);

            foreach (var (key, column, width) in Copies)
            {
                migrationBuilder.Sql($@"
UPDATE t SET t.[{column}] = LEFT(v.FieldValue, {width})
FROM Tenants t
CROSS APPLY (
    SELECT TOP 1 FieldValue FROM ApplicationSetupValues
    WHERE TenantId = t.Id AND FieldKey = '{key}' AND FieldValue IS NOT NULL AND LTRIM(RTRIM(FieldValue)) <> ''
    ORDER BY ISNULL(UpdatedAt, CreatedAt) DESC) v
WHERE t.[{column}] IS NULL OR LTRIM(RTRIM(t.[{column}])) = '';");
            }
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "TargetAudience",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TargetCustomerType",
                table: "Tenants");

            migrationBuilder.DropColumn(
                name: "TargetLocation",
                table: "Tenants");
        }
    }
}
