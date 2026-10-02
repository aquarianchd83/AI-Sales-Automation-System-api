using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class MakeCustomerTagNameUniquePerTenant : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Tags are tenant-owned, but the old unique index was on Name alone - so one tenant's "VIP" (or a lead-discovery date tag)
            // blocked every other tenant from creating the same name. Existing rows already satisfy the narrower rule.
            migrationBuilder.DropIndex(
                name: "IX_CustomerTags_Name",
                table: "CustomerTags");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTags_TenantId_Name",
                table: "CustomerTags",
                columns: new[] { "TenantId", "Name" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CustomerTags_TenantId_Name",
                table: "CustomerTags");

            migrationBuilder.CreateIndex(
                name: "IX_CustomerTags_Name",
                table: "CustomerTags",
                column: "Name",
                unique: true);
        }
    }
}
