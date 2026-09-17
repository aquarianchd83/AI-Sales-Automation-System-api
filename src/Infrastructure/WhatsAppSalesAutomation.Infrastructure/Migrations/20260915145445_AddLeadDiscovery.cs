using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadDiscovery : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Existing plans get the caps PlanSeeder gives a fresh database, not 0 - which would silently switch
            // lead discovery off for every subscribed tenant.
            migrationBuilder.AddColumn<int>(
                name: "MaxLeadDiscoveryBatchSize",
                table: "Plans",
                type: "int",
                nullable: false,
                defaultValue: 25);
            migrationBuilder.Sql("UPDATE Plans SET MaxLeadDiscoveryBatchSize = 100 WHERE Code = 'growth'");
            migrationBuilder.Sql("UPDATE Plans SET MaxLeadDiscoveryBatchSize = 250 WHERE Code = 'scale'");

            migrationBuilder.CreateTable(
                name: "DiscoveredLeads",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    BusinessName = table.Column<string>(type: "nvarchar(300)", maxLength: 300, nullable: false),
                    BusinessType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    ContactPerson = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: true),
                    Address = table.Column<string>(type: "nvarchar(500)", maxLength: 500, nullable: true),
                    City = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    State = table.Column<string>(type: "nvarchar(100)", maxLength: 100, nullable: true),
                    Phone = table.Column<string>(type: "nvarchar(50)", maxLength: 50, nullable: true),
                    PhoneE164 = table.Column<string>(type: "nvarchar(20)", maxLength: 20, nullable: true),
                    PhoneVerified = table.Column<bool>(type: "bit", nullable: false),
                    PhoneSourceUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    Email = table.Column<string>(type: "nvarchar(256)", maxLength: 256, nullable: true),
                    Website = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: true),
                    SourceUrl = table.Column<string>(type: "nvarchar(2000)", maxLength: 2000, nullable: false),
                    LeadScore = table.Column<int>(type: "int", nullable: false),
                    ScoreRationale = table.Column<string>(type: "nvarchar(1000)", maxLength: 1000, nullable: true),
                    PhoneKey = table.Column<string>(type: "nvarchar(15)", maxLength: 15, nullable: true),
                    WebsiteKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: true),
                    NameKey = table.Column<string>(type: "nvarchar(450)", maxLength: 450, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_DiscoveredLeads", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "LeadDiscoveryProfiles",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    TenantId = table.Column<Guid>(type: "uniqueidentifier", nullable: false),
                    IsEnabled = table.Column<bool>(type: "bit", nullable: false),
                    TargetBusinessType = table.Column<string>(type: "nvarchar(200)", maxLength: 200, nullable: false),
                    Keywords = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    Locations = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    BatchSize = table.Column<int>(type: "int", nullable: false),
                    RequiredFields = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    PhoneRequired = table.Column<bool>(type: "bit", nullable: false),
                    EmailRequired = table.Column<bool>(type: "bit", nullable: false),
                    IndependentBusiness = table.Column<bool>(type: "bit", nullable: false),
                    MinimumLeadScore = table.Column<int>(type: "int", nullable: false),
                    AdditionalCriteria = table.Column<string>(type: "nvarchar(max)", nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "datetime2", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "datetime2", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_LeadDiscoveryProfiles", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredLeads_TenantId_CreatedAt",
                table: "DiscoveredLeads",
                columns: new[] { "TenantId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredLeads_TenantId_NameKey",
                table: "DiscoveredLeads",
                columns: new[] { "TenantId", "NameKey" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredLeads_TenantId_PhoneKey",
                table: "DiscoveredLeads",
                columns: new[] { "TenantId", "PhoneKey" });

            migrationBuilder.CreateIndex(
                name: "IX_DiscoveredLeads_TenantId_WebsiteKey",
                table: "DiscoveredLeads",
                columns: new[] { "TenantId", "WebsiteKey" });

            migrationBuilder.CreateIndex(
                name: "IX_LeadDiscoveryProfiles_TenantId",
                table: "LeadDiscoveryProfiles",
                column: "TenantId",
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "DiscoveredLeads");

            migrationBuilder.DropTable(
                name: "LeadDiscoveryProfiles");

            migrationBuilder.DropColumn(
                name: "MaxLeadDiscoveryBatchSize",
                table: "Plans");
        }
    }
}
