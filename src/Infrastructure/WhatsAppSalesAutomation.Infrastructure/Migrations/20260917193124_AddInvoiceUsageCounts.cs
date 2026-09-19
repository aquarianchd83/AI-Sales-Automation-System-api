using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoiceUsageCounts : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<int>(
                name: "AiInteractionsCount",
                table: "Invoices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LeadDiscoveryLeadsCount",
                table: "Invoices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "LeadDiscoveryRunsCount",
                table: "Invoices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "MessageLimit",
                table: "Invoices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "MessagesSentCount",
                table: "Invoices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserCount",
                table: "Invoices",
                type: "int",
                nullable: false,
                defaultValue: 0);

            migrationBuilder.AddColumn<int>(
                name: "UserLimit",
                table: "Invoices",
                type: "int",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "WhatsAppBillableMessagesCount",
                table: "Invoices",
                type: "int",
                nullable: false,
                defaultValue: 0);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AiInteractionsCount",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "LeadDiscoveryLeadsCount",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "LeadDiscoveryRunsCount",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "MessageLimit",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "MessagesSentCount",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "UserCount",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "UserLimit",
                table: "Invoices");

            migrationBuilder.DropColumn(
                name: "WhatsAppBillableMessagesCount",
                table: "Invoices");
        }
    }
}
