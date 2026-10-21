using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddCreditPayers : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "CreditScenarioSettings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scenario = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    Payer = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    UnitsPerAction = table.Column<decimal>(type: "decimal(18,4)", nullable: true),
                    UpdatedByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditScenarioSettings", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "CreditUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Scenario = table.Column<string>(type: "nvarchar(40)", maxLength: 40, nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Units = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    ConfiguredPayer = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    ChargedTo = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    FellBackToTenant = table.Column<bool>(type: "bit", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ReferenceType = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    ReferenceId = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Description = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    SettledAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    SettledByUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    SettlementNote = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_CreditUsages", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformCreditEntries",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    EntryType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UnitsDelta = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    BalanceAfter = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    OperationKey = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    Note = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    ActorUserId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    OccurredAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformCreditEntries", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "PlatformCreditWallets",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Balance = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    RowVersion = table.Column<byte[]>(type: "rowversion", rowVersion: true, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlatformCreditWallets", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_CreditScenarioSettings_Scenario",
                table: "CreditScenarioSettings",
                column: "Scenario",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_CreditUsages_OccurredAtUtc",
                table: "CreditUsages",
                column: "OccurredAtUtc");

            migrationBuilder.CreateIndex(
                name: "IX_CreditUsages_Scenario_OccurredAtUtc",
                table: "CreditUsages",
                columns: new[] { "Scenario", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditUsages_TenantId_OccurredAtUtc",
                table: "CreditUsages",
                columns: new[] { "TenantId", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_CreditUsages_TenantId_OperationKey",
                table: "CreditUsages",
                columns: new[] { "TenantId", "OperationKey" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCreditEntries_OperationKey",
                table: "PlatformCreditEntries",
                column: "OperationKey");

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCreditEntries_QuotaType_OccurredAtUtc",
                table: "PlatformCreditEntries",
                columns: new[] { "QuotaType", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlatformCreditWallets_QuotaType",
                table: "PlatformCreditWallets",
                column: "QuotaType",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "CreditScenarioSettings");

            migrationBuilder.DropTable(
                name: "CreditUsages");

            migrationBuilder.DropTable(
                name: "PlatformCreditEntries");

            migrationBuilder.DropTable(
                name: "PlatformCreditWallets");
        }
    }
}
