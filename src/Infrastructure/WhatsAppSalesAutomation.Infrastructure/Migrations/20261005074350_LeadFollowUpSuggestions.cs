using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class LeadFollowUpSuggestions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LeadFollowUps_LeadId_Scheduled",
                table: "LeadFollowUps");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageTemplateId",
                table: "LeadFollowUps",
                type: "uniqueidentifier",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier");

            migrationBuilder.CreateIndex(
                name: "IX_LeadFollowUps_LeadId_Open",
                table: "LeadFollowUps",
                column: "LeadId",
                unique: true,
                filter: "[Status] IN ('Scheduled', 'Suggested')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_LeadFollowUps_LeadId_Open",
                table: "LeadFollowUps");

            migrationBuilder.AlterColumn<Guid>(
                name: "MessageTemplateId",
                table: "LeadFollowUps",
                type: "uniqueidentifier",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uniqueidentifier",
                oldNullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_LeadFollowUps_LeadId_Scheduled",
                table: "LeadFollowUps",
                column: "LeadId",
                unique: true,
                filter: "[Status] = 'Scheduled'");
        }
    }
}
