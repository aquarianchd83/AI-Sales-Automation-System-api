using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadDiscoveryCachedLeads : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "LeadDiscoveryCachedLeads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    LeadDiscoveryProfileId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    SourceExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    PhoneKey = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    NameKey = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: false),
                    CandidateJson = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CandidateDetailsJson = table.Column<string>(type: "nvarchar(max)", nullable: true),
                    ExpiresAtUtc = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UsedAtUtc = table.Column<DateTime>(type: "datetime2", nullable: true),
                    UsedExecutionId = table.Column<Guid>(type: "uniqueidentifier", nullable: true),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryCachedLeads", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryCachedLeads_TenantId_LeadDiscoveryProfileId_UsedAtUtc",
                table: "LeadDiscoveryCachedLeads",
                columns: new[] { "TenantId", "LeadDiscoveryProfileId", "UsedAtUtc" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "LeadDiscoveryCachedLeads");
        }
    }
}
