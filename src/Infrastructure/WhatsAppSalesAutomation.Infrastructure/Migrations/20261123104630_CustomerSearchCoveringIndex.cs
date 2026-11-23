using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class CustomerSearchCoveringIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId_CreatedAt",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_CreatedAt",
                table: "Customers",
                columns: new[] { "TenantId", "CreatedAt" },
                filter: "[IsDeleted] = 0")
                .Annotation("SqlServer:Include", new[] { "PhoneNumberE164", "FirstName", "LastName", "Email" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Customers_TenantId_CreatedAt",
                table: "Customers");

            migrationBuilder.CreateIndex(
                name: "IX_Customers_TenantId_CreatedAt",
                table: "Customers",
                columns: new[] { "TenantId", "CreatedAt" },
                filter: "[IsDeleted] = 0");
        }
    }
}
