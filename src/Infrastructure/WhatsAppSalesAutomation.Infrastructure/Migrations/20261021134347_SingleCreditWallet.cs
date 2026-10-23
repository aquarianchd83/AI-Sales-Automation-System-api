using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class SingleCreditWallet : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // The single credit wallet replaces five separate balances. Development data only: the old wallets, grants, ledger rows and spend records are
            // cleared, and the start-up seeder puts every tenant on a plan, which grants its credits. Payments, refunds and the plans themselves are kept.
            migrationBuilder.Sql("DELETE FROM QuotaLedgerEntries");
            migrationBuilder.Sql("DELETE FROM QuotaGrants");
            migrationBuilder.Sql("DELETE FROM QuotaWallets");
            migrationBuilder.Sql("DELETE FROM PlatformCreditEntries");
            migrationBuilder.Sql("DELETE FROM PlatformCreditWallets");
            migrationBuilder.Sql("DELETE FROM CreditUsages");

            migrationBuilder.DropTable(
                name: "PlanQuotas");

            migrationBuilder.AddColumn<string>(
                name: "Feature",
                table: "QuotaLedgerEntries",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: true);

            migrationBuilder.AddColumn<decimal>(
                name: "MonthlyCredits",
                table: "Plans",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "StarterCredits",
                table: "Plans",
                type: "decimal(18,4)",
                nullable: false,
                defaultValue: 0m);

            migrationBuilder.AddColumn<decimal>(
                name: "CreditsPerUnit",
                table: "CreditScenarioSettings",
                type: "decimal(18,4)",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "PlanFeatures",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanFeatures", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantFeatureCaps",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    MonthlyLimit = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantFeatureCaps", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_QuotaLedgerEntries_TenantId_Feature_OccurredAtUtc",
                table: "QuotaLedgerEntries",
                columns: new[] { "TenantId", "Feature", "OccurredAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_PlanFeatures_PlanId_Feature",
                table: "PlanFeatures",
                columns: new[] { "PlanId", "Feature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantFeatureCaps_TenantId_Feature",
                table: "TenantFeatureCaps",
                columns: new[] { "TenantId", "Feature" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanFeatures");

            migrationBuilder.DropTable(
                name: "TenantFeatureCaps");

            migrationBuilder.DropIndex(
                name: "IX_QuotaLedgerEntries_TenantId_Feature_OccurredAtUtc",
                table: "QuotaLedgerEntries");

            migrationBuilder.DropColumn(
                name: "Feature",
                table: "QuotaLedgerEntries");

            migrationBuilder.DropColumn(
                name: "MonthlyCredits",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "StarterCredits",
                table: "Plans");

            migrationBuilder.DropColumn(
                name: "CreditsPerUnit",
                table: "CreditScenarioSettings");

            migrationBuilder.CreateTable(
                name: "PlanQuotas",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    IncludedUnits = table.Column<decimal>(type: "decimal(18,4)", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    QuotaType = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanQuotas", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanQuotas_PlanId_QuotaType",
                table: "PlanQuotas",
                columns: new[] { "PlanId", "QuotaType" },
                unique: true);
        }
    }
}
