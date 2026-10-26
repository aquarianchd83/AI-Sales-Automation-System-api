using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class ScopeMessageIdByProvider : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_WhatsAppMessageId",
                table: "Messages");

            migrationBuilder.AddColumn<string>(
                name: "Provider",
                table: "Messages",
                type: "nvarchar(20)",
                maxLength: 20,
                nullable: false,
                defaultValue: "Meta");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_Provider_WhatsAppMessageId",
                table: "Messages",
                columns: new[] { "Provider", "WhatsAppMessageId" },
                unique: true,
                filter: "[WhatsAppMessageId] IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Messages_Provider_WhatsAppMessageId",
                table: "Messages");

            migrationBuilder.DropColumn(
                name: "Provider",
                table: "Messages");

            migrationBuilder.CreateIndex(
                name: "IX_Messages_WhatsAppMessageId",
                table: "Messages",
                column: "WhatsAppMessageId",
                unique: true,
                filter: "[WhatsAppMessageId] IS NOT NULL");
        }
    }
}
