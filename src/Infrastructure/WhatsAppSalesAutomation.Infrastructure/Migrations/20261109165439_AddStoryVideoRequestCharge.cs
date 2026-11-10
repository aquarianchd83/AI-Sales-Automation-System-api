using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace WhatsAppSalesAutomation.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddStoryVideoRequestCharge : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "ChargeActivityId",
                table: "StoryVideoRequests",
                type: "uniqueidentifier",
                nullable: true);

            migrationBuilder.AddColumn<DateTime>(
                name: "RejectedAtUtc",
                table: "StoryVideoRequests",
                type: "datetime2",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RejectedReason",
                table: "StoryVideoRequests",
                type: "nvarchar(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ChargeActivityId",
                table: "StoryVideoRequests");

            migrationBuilder.DropColumn(
                name: "RejectedAtUtc",
                table: "StoryVideoRequests");

            migrationBuilder.DropColumn(
                name: "RejectedReason",
                table: "StoryVideoRequests");
        }
    }
}
