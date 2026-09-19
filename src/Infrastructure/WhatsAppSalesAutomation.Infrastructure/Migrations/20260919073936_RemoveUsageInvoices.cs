using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class RemoveUsageInvoices : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "Invoices");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Invoices",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    AiConversationAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    AiConversationAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    AiInteractionsCount = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CurrencyCode = table.Column<string>(type: "nvarchar(3)", maxLength: 3, nullable: false),
                    CurrencySymbol = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    LeadDiscoveryAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    LeadDiscoveryAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    LeadDiscoveryLeadsCount = table.Column<int>(type: "int", nullable: false),
                    LeadDiscoveryRunsCount = table.Column<int>(type: "int", nullable: false),
                    MessageLimit = table.Column<int>(type: "int", nullable: true),
                    MessagesSentCount = table.Column<int>(type: "int", nullable: false),
                    PaidAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    PeriodEndUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PeriodStartUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    PlanName = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Status = table.Column<string>(type: "nvarchar(10)", maxLength: 10, nullable: false),
                    SubscriptionAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    SubscriptionAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TotalAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    TotalAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UserCount = table.Column<int>(type: "int", nullable: false),
                    UserLimit = table.Column<int>(type: "int", nullable: true),
                    WhatsAppAmountLocal = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    WhatsAppAmountUsd = table.Column<decimal>(type: "decimal(18,6)", nullable: false),
                    WhatsAppBillableMessagesCount = table.Column<int>(type: "int", nullable: false)
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
    }
}
