using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddPlanFeatureTrials : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "PlanFeatureTrials",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    PlanId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    FreeUses = table.Column<int>(type: "int", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_PlanFeatureTrials", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "TenantFeatureTrialUsages",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    Feature = table.Column<string>(type: "nvarchar(30)", maxLength: 30, nullable: false),
                    Used = table.Column<int>(type: "int", nullable: false),
                    FirstUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    LastUsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TenantFeatureTrialUsages", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_PlanFeatureTrials_PlanId_Feature",
                table: "PlanFeatureTrials",
                columns: new[] { "PlanId", "Feature" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TenantFeatureTrialUsages_TenantId_Feature",
                table: "TenantFeatureTrialUsages",
                columns: new[] { "TenantId", "Feature" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "PlanFeatureTrials");

            migrationBuilder.DropTable(
                name: "TenantFeatureTrialUsages");
        }
    }
}
