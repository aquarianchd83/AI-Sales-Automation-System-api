using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadDiscoveryScheduleSlots : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeadDiscoveryScheduleSlots",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    NextRunAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    RepeatDaily = table.Column<bool>(type: "bit", nullable: false),
                    IsDone = table.Column<bool>(type: "bit", nullable: false),
                    LastRunAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryScheduleSlots", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryScheduleSlots_IsDone_NextRunAtUtc",
                table: "LeadDiscoveryScheduleSlots",
                columns: new[] { "IsDone", "NextRunAtUtc" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryScheduleSlots_TenantId",
                table: "LeadDiscoveryScheduleSlots",
                column: "TenantId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadDiscoveryScheduleSlots");
        }
    }
}
