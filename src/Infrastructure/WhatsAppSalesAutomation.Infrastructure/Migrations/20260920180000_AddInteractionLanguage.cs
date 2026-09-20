using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddInteractionLanguage : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // Per-turn language reading. Customer.PreferredLanguage already held the answer, but only
            // the latest one - and the rule for changing it needs the previous turn to compare
            // against, so one turn's reading has to survive into the next.
            migrationBuilder.AddColumn<string>(
                name: "DetectedLanguage",
                table: "AiInteractions",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(name: "DetectedLanguage", table: "AiInteractions");
        }
    }
}
