using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddLeadDiscoveryProfileAutoCampaignStart : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "AutoCampaignStartMode",
                table: "LeadDiscoveryProfiles",
                type: "nvarchar(30)",
                maxLength: 30,
                nullable: false,
                defaultValue: "Immediate");

            migrationBuilder.AddColumn<TimeSpan>(
                name: "AutoCampaignStartTime",
                table: "LeadDiscoveryProfiles",
                type: "time",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "AutoCampaignStartMode",
                table: "LeadDiscoveryProfiles");

            migrationBuilder.DropColumn(
                name: "AutoCampaignStartTime",
                table: "LeadDiscoveryProfiles");
        }
    }
}
