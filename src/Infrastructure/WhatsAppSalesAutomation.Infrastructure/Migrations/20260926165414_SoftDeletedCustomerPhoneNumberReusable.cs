using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SoftDeletedCustomerPhoneNumberReusable : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId_PhoneNumberE164",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_PhoneNumberE164",
                table: "Customers",
                columns: new[] { "TenantId", "PhoneNumberE164" },
                unique: true,
                filter: "[IsDeleted] = 0");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId_PhoneNumberE164",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_PhoneNumberE164",
                table: "Customers",
                columns: new[] { "TenantId", "PhoneNumberE164" },
                unique: true);
        }
    }
}
