using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PlanName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    SubscriptionAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    LeadDiscoveryAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    WhatsAppAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    AiConversationAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TotalAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    CurrencySymbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SubscriptionAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    LeadDiscoveryAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    WhatsAppAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    AiConversationAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TotalAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Invoices", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Invoices_TenantId_PeriodStartUtc",
                table: "Invoices",
                columns: new[] { "TenantId", "PeriodStartUtc" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Invoices");
        }
    }
}
