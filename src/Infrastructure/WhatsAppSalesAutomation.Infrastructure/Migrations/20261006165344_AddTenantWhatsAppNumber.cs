using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTenantWhatsAppNumber : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "WhatsAppNumber",
                table: "Tenants",
                type: "nvarchar(32)",
                maxLength: 32,
                nullable: true);

            // A tenant whose WhatsApp connection Meta has already confirmed has plainly got its number: keep it, so an
            // established tenant is not asked for it again.
            migrationBuilder.Sql(@"
UPDATE t SET t.WhatsAppNumber = LEFT(c.VerifiedDisplayPhoneNumber, 32)
FROM Tenants t
JOIN TenantWhatsAppConfigs c ON c.TenantId = t.Id
WHERE (t.WhatsAppNumber IS NULL OR LTRIM(RTRIM(t.WhatsAppNumber)) = '')
  AND c.VerifiedDisplayPhoneNumber IS NOT NULL AND LTRIM(RTRIM(c.VerifiedDisplayPhoneNumber)) <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "WhatsAppNumber",
                table: "Tenants");
        }
    }
}
