using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <summary>
    /// Signup now starts a tenant's contact details from the address given at registration. This does the same for
    /// tenants that already exist: where the Business Profile's support email (or phone) is still blank, use the owner's
    /// own email (or phone). Anything a tenant has typed is left alone. Data only - no schema change.
    /// </summary>
    public partial class BackfillTenantContactFromOwner : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql(@"
UPDATE t SET t.SupportEmail = LEFT(u.Email, 256)
FROM Tenants t
JOIN Users u ON u.Id = t.OwnerUserId
WHERE (t.SupportEmail IS NULL OR LTRIM(RTRIM(t.SupportEmail)) = '')
  AND u.Email IS NOT NULL AND LTRIM(RTRIM(u.Email)) <> '';");

            migrationBuilder.Sql(@"
UPDATE t SET t.SupportPhone = LEFT(u.PhoneNumber, 32)
FROM Tenants t
JOIN Users u ON u.Id = t.OwnerUserId
WHERE (t.SupportPhone IS NULL OR LTRIM(RTRIM(t.SupportPhone)) = '')
  AND u.PhoneNumber IS NOT NULL AND LTRIM(RTRIM(u.PhoneNumber)) <> '';");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Data backfill: there is no telling the copied values from ones a tenant typed, so nothing to undo.
        }
    }
}
