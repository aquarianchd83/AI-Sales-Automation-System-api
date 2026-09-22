using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddAutoCampaignEnrollment : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<bool>(
                name: "AutoCampaignEnabled",
                table: "LeadDiscoveryProfiles",
                type: "bit",
                nullable: false,
                defaultValue: false);

            migrationBuilder.AddColumn<Guid>(
                name: "SourceCampaignId",
                table: "LeadDiscoveryProfiles",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AutoCampaignEnrollments",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceCampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    ExecutionCampaignId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    DiscoveredLeadId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    CustomerId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    ExecutionDateLocal = table.Column<DateTime>(type: "datetime2", nullable: false),
                    Status = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: false),
                    Reason = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AutoCampaignEnrollments", x => x.Id);
                });

            // "Today's execution campaign for this source campaign" lookup.
            migrationBuilder.CreateIndex(
                name: "IX_AutoCampaignEnrollments_TenantId_SourceCampaignId_ExecutionDateLocal",
                table: "AutoCampaignEnrollments",
                columns: new[] { "TenantId", "SourceCampaignId", "ExecutionDateLocal" });

            // Idempotency guard: a customer may have at most one Started row per (tenant, source
            // campaign). Filtered rather than a plain unique index, because Skipped/Failed rows are
            // expected to repeat for the same customer/source pair.
            migrationBuilder.CreateIndex(
                name: "IX_AutoCampaignEnrollments_TenantId_SourceCampaignId_CustomerId",
                table: "AutoCampaignEnrollments",
                columns: new[] { "TenantId", "SourceCampaignId", "CustomerId" },
                unique: true,
                filter: "[Status] = 'Started'");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AutoCampaignEnrollments");

            migrationBuilder.DropColumn(
                name: "AutoCampaignEnabled",
                table: "LeadDiscoveryProfiles");

            migrationBuilder.DropColumn(
                name: "SourceCampaignId",
                table: "LeadDiscoveryProfiles");
        }
    }
}
